﻿using IdGen;
using LightInject;
using Microsoft.Extensions.Logging;
using Nethereum.Contracts.QueryHandlers.MultiCall;
using Newtonsoft.Json;
using PLang.Building.Model;
using PLang.Container;
using PLang.Errors;
using PLang.Errors.AskUser;
using PLang.Errors.Builder;
using PLang.Errors.Methods;
using PLang.Errors.Runtime;
using PLang.Exceptions;
using PLang.Exceptions.AskUser;
using PLang.Interfaces;
using PLang.Models;
using PLang.Runtime;
using PLang.SafeFileSystem;
using PLang.Services.LlmService;
using PLang.Services.OutputStream;
using PLang.Utils;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using static PLang.Modules.BaseBuilder;
using Instruction = PLang.Building.Model.Instruction;

namespace PLang.Modules
{
	public abstract class BaseProgram
	{
		private HttpListenerContext? _listenerContext = null;

		protected MemoryStack memoryStack;
		protected Goal goal;
		protected GoalStep goalStep;
		protected Instruction instruction;
		protected PLangAppContext context;
		protected VariableHelper variableHelper;
		protected ITypeHelper typeHelper;
		protected GenericFunction function;
		private ILlmServiceFactory llmServiceFactory;
		private ILogger logger;
		private IServiceContainer container;
		private IAppCache appCache;
		private IOutputStreamFactory outputStreamFactory;
		private IPLangFileSystem fileSystem;
		private MethodHelper methodHelper;
		private IFileAccessHandler fileAccessHandler;
		private IAskUserHandlerFactory askUserHandlerFactory;
		private ISettings settings;
		protected bool IsBuilder { get; } = false;
		public HttpListenerContext? HttpListenerContext
		{
			get
			{
				if (_listenerContext == null && context.ContainsKey(ReservedKeywords.IsHttpRequest)) throw new NullReferenceException("_listenerContext is null. It should not be null");

				return _listenerContext;
			}
		}
		public Goal Goal { get { return goal; } }

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
		public BaseProgram()
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
		{
			AppContext.TryGetSwitch("Builder", out bool isBuilder);
			IsBuilder = isBuilder;
		}

		public void Init(IServiceContainer container, Goal goal, GoalStep step, Instruction instruction, HttpListenerContext? httpListenerContext)
		{
			this.container = container;

			this.logger = container.GetInstance<ILogger>();
			this.memoryStack = container.GetInstance<MemoryStack>();
			this.context = container.GetInstance<PLangAppContext>();
			this.appCache = container.GetInstance<IAppCache>();
			this.outputStreamFactory = container.GetInstance<IOutputStreamFactory>();
			this.fileSystem = container.GetInstance<IPLangFileSystem>();
			this.askUserHandlerFactory = container.GetInstance<IAskUserHandlerFactory>();
			this.settings = container.GetInstance<ISettings>();
			_listenerContext = httpListenerContext;

			this.goal = goal;
			this.goalStep = step;
			this.instruction = instruction;

			variableHelper = container.GetInstance<VariableHelper>();
			this.typeHelper = container.GetInstance<ITypeHelper>();
			this.llmServiceFactory = container.GetInstance<ILlmServiceFactory>();
			methodHelper = new MethodHelper(goalStep, variableHelper, typeHelper);
			fileAccessHandler = container.GetInstance<IFileAccessHandler>();
		}

		public IServiceContainer Container { get { return container; } }


		public virtual async Task<(object? ReturnValue, IError? Error)> Run()
		{
			var functions = instruction.GetFunctions();
			foreach (var function in functions)
			{
				// no support for multiple functions, return on first
				return await RunFunction(function);
			}
			return (null, new ProgramError("Nothing found to run", goalStep, function, FixSuggestion: "Try rebuilding your code"));
		}

		private T GetModule<T>() where T : BaseProgram
		{
			return (T)container.GetInstance(typeof(T));
		}

		public void SetStep(GoalStep step)
		{
			this.goalStep = step;
			this.goal = step.Goal;
		}

		public async Task<(object? ReturnValue, IError? Error)> RunFunction(GenericFunction function)
		{

			Dictionary<string, object?>? parameterValues = null;
			this.function = function; // this is to give sub classes access to current function running.
			try
			{
				MethodInfo? method = await methodHelper.GetMethod(this, function);
				if (method == null)
				{
					return (null, new StepError($"Could not load method {function.FunctionName} to run", goalStep, "MethodNotFound", 500));
				}

				logger.LogDebug("Method:{0}.{1}({2})", goalStep.ModuleType, method.Name, method.GetParameters());

				//TODO: Should move this caching check up the call stack. code is doing to much work before returning cache
				if (await LoadCached(method, function)) return (null, null);

				if (method.ReturnType != typeof(Task) && method.ReturnType.BaseType != typeof(Task))
				{
					return (new Error($"The method {method.Name} does not return Task. Method that are called must return Task"), null);
				}

				parameterValues = methodHelper.GetParameterValues(method, function);
				logger.LogTrace("Parameters:{0}", parameterValues);

				// This is for memoryStack event handler. Should find a better way
				context.AddOrReplace(ReservedKeywords.Goal, goal);

				Task? task = null;
				try
				{
					task = method.Invoke(this, parameterValues.Values.ToArray()) as Task;
				}
				catch (System.ArgumentException ex)
				{
					if (ex.Message.Contains("converted to type"))
					{
						string type = ex.Message.Substring(ex.Message.IndexOf("to type") + 9).Replace("'", "").TrimEnd('.');
						var parameter = method.GetParameters().FirstOrDefault(p => p.ParameterType.FullName == type);
						if (parameter != null)
						{
							var functionParameter = function.Parameters.FirstOrDefault(p => p.Name == parameter.Name);
							if (functionParameter != null)
							{
								return (null, new InvalidParameterError(function.FunctionName, $"{functionParameter.Value} is not corrent value to call {function.FunctionName}. It should be type of {type}. Is the step loading {functionParameter.Value} correct?", goalStep, FixSuggestion: $"Check if the step that loads {functionParameter.Value} is correctly mapped."));
							}
						}
					}
					return (null, new InvalidParameterError(function.FunctionName, ex.Message, goalStep, FixSuggestion: "Step is likely built on an older verion of plang. Try to rebuild code"));
				}

				if (task == null)
				{
					logger.LogWarning("Method called is not an async function. Make sure it is defined as 'public async Task' or 'public async Task<YourReturnType>'");
					return (null, null);
				}

				if (goalStep.WaitForExecution)
				{
					try
					{
						await task;
					}
					catch { }
				}
				if (task.Status == TaskStatus.Canceled)
				{
					return (null, new CancelledError(goal, goalStep, function));
				}
				if (task.Status == TaskStatus.Faulted && task.Exception != null)
				{
					var ex = task.Exception.InnerException ?? task.Exception;
					if (ex is FileAccessException fa)
					{
						return await HandleFileAccess(fa);
					}

					if (ex is MissingSettingsException mse)
					{
						var settingsError = new AskUserError(mse.Message, async (object[]? result) =>
						{
							var value = result?[0] ?? null;
							if (value is Array) value = ((object[])value)[0];

							await mse.InvokeCallback(value);
							return (true, null);
						});

						(var isHandled, var handlerError) = await askUserHandlerFactory.CreateHandler().Handle(settingsError);
						if (isHandled) return await RunFunction(function);
					}

					var pe = new ProgramError(ex.Message, goalStep, function, parameterValues, Exception: ex, Key: ex.GetType().FullName ?? "ProgramError");

					if (this is IDisposable disposable)
					{
						//logger.LogDebug($"Calling Dispose for {this}");
						//disposable.Dispose();
					}
					return (null, pe);
				}

				if (!goalStep.WaitForExecution || method.ReturnType == typeof(Task))
				{
					return (null, null);
				}

				(object? result, var error, var properties) = GetValuesFromTask(task);

				if (error != null)
				{
					if (error is AskUserError aue)
					{
						(var isHandled, var handlerError) = await HandleAskUser(aue);

						if (isHandled) return await RunFunction(function);

						return (result, ErrorHelper.GetMultipleError(error, handlerError));
					}
					if (error.Step == null)
					{
						error.Step = goalStep;
					}
					if (error is ProgramError pe && pe.GenericFunction is null)
					{
						pe.GenericFunction = function;
					}

					if (error.Goal == null)
					{
						error.Goal = goal;
					}
					return (result, error);
				}

				SetReturnValue(function, result, properties);

				await SetCachedItem(result);
				return (result, null);

			}
			catch (Exception ex)
			{
				if (ex is MissingSettingsException mse)
				{
					var settingsError = new AskUserError(mse.Message, async (object[]? result) =>
					{
						var value = result?[0] ?? null;
						if (value is Array) value = ((object[])value)[0];

						await mse.InvokeCallback(value);
						return (true, null);
					});

					(var isHandled, var handlerError) = await askUserHandlerFactory.CreateHandler().Handle(settingsError);
					if (isHandled) return await RunFunction(function);
				}
				var pe = new ProgramError(ex.Message, goalStep, function, parameterValues, Key: ex.GetType().FullName ?? "ProgramError", 500, Exception: ex);

				return (null, pe);
			}
		}

		private async Task<(bool, IError?)> HandleAskUser(AskUserError aue)
		{
			(var isHandled, var handlerError) = await askUserHandlerFactory.CreateHandler().Handle(aue);
			if (handlerError is AskUserError aueSecond)
			{
				return await HandleAskUser(aueSecond);
			}
			return (isHandled, handlerError);
		}

		private async Task<(object?, IError?)> HandleFileAccess(FileAccessException fa)
		{
			var fileAccessHandler = container.GetInstance<IFileAccessHandler>();
			var askUserFileAccess = new AskUserFileAccess(fa.AppName, fa.Path, fa.Message, fileAccessHandler.ValidatePathResponse);

			(var isHandled, var handlerError) = await askUserHandlerFactory.CreateHandler().Handle(askUserFileAccess);
			if (isHandled) return await RunFunction(function);

			return (null, ErrorHelper.GetMultipleError(askUserFileAccess, handlerError));
		}

		private (object? returnValue, IError? error, IProperties? Properties) GetValuesFromTask(Task task)
		{

			Type taskType = task.GetType();
			var returnArguments = taskType.GetGenericArguments().FirstOrDefault();
			if (returnArguments == null) return (null, null, null);

			if (returnArguments == typeof(IError))
			{
				var resultTask = task as Task<IError?>;
				return (null, resultTask?.Result, null);
			}
			if (returnArguments == typeof(IProperties))
			{
				var resultTask = task as Task<IProperties?>;
				return (null, null, resultTask?.Result);
			}
			/*
			if (!returnArguments.FullName!.StartsWith("System.ValueTuple"))
			{
				var resultProperty = taskType.GetProperty("Result");
				return (resultProperty.GetValue(task), null, null);

			}*/

			object? value = null;
			IError? error = null;
			IProperties? properties = null;

			var resultProperty = taskType.GetProperty("Result");
			if (resultProperty == null)
			{
				throw new Exception("This should not happen trying to get Result");
			}


			var rawResult = resultProperty.GetValue(task);
			if (rawResult == null)
			{
				return (null, null, null);
			}
			if (rawResult is IError resultError) return (null, resultError, null);
			if (rawResult is IProperties resultProperties) return (null, null, resultProperties);

			var result = (dynamic?)rawResult;
			if (!result?.GetType().ToString().Contains("ValueTuple"))
			{
				if (returnArguments is IError)
				{
					error = result as IError;
				}
				else if (returnArguments is IProperties)
				{
					properties = result as IProperties;
				}
				else
				{
					if (result is Return @return)
					{
						value = @return.Variables;
					}
					else
					{
						value = result;
					}
				}

			}
			else
			{

				var fields = returnArguments.GetFields();
				if (fields.Length == 0) throw new Exception("This should not happen, fields is 0 length from Task<,,>");

				foreach (var field in fields)
				{
					var fieldValue = field.GetValue(result);
					if (field.FieldType == typeof(IError))
					{
						error = fieldValue as IError;
					}
					else if (field.FieldType == typeof(IProperties))
					{
						properties = fieldValue as IProperties;
					}
					else
					{
						value = fieldValue;
					}

				}
			}
			return (value, error, properties);

			/*
			if (fields[0] == typeof(IError))
			{
				// It's a Task<Error?>
				var resultTask = task as Task<IError?>;
				return (null, resultTask?.Result, null);
			}
			else if (fields.Count() > 1)
			{
				try
				{
					var resultProperty = taskType.GetProperty("Result");
					
					var result = (dynamic?)resultProperty.GetValue(task);
					

					
					if (fields.Count() == 3)
					{
						return (result.Item1, result.Item2 as IError, result.Item3 as IProperties);
					}
					return (result.Item1, result.Item2 as IError, null);
				}
				catch (TargetInvocationException ex)
				{
					return (null, new ExceptionError(ex.InnerException ?? ex), null);
				}
			}
			else
			{
				var resultTask = task as Task<object?>;
				return (resultTask, null, null);
			}*/

		}

		private void SetReturnValue(GenericFunction function, object? result, IProperties properties)
		{
			if (function.ReturnValues == null || function.ReturnValues.Count == 0) return;


			if (result == null)
			{
				foreach (var returnValue in function.ReturnValues)
				{
					memoryStack.Put(returnValue.VariableName, null, properties: properties);
				}
			}
			else if (result is IReturnDictionary || result.GetType().Name == "DapperRow")
			{
				var dict = (IDictionary<string, object>)result;
				if (result is IReturnDictionary rd)
				{
					int idx = 0;
					foreach (var key in dict.Keys)
					{
						var variableName = function.ReturnValues.FirstOrDefault(p => p.VariableName == key.Replace("%", ""))?.VariableName;
						if (variableName != null)
						{
							memoryStack.Put(variableName, dict[key], properties: properties);
						}
						else if (idx < function.ReturnValues.Count)
						{
							memoryStack.Put(function.ReturnValues[idx].VariableName, dict[key], properties: properties);
						}
						idx++;
					}
				}
				else
				{
					foreach (var returnValue in function.ReturnValues)
					{
						var key = dict.Keys.FirstOrDefault(p => p.Replace("%", "").ToLower() == returnValue.VariableName.Replace("%", "").ToLower());
						if (key == null)
						{
							memoryStack.Put(returnValue.VariableName, dict, properties: properties);
							continue;
						}

						memoryStack.Put(returnValue.VariableName, dict[key], properties: properties);
					}
				}
			}
			else
			{
				foreach (var returnValue in function.ReturnValues)
				{
					memoryStack.Put(returnValue.VariableName, result, properties: properties);
				}
			}
		}

		private async Task SetCachedItem(object? result)
		{
			if (result == null || goalStep == null || goalStep.CacheHandler == null) return;
			if (goalStep?.CacheHandler?.CacheKey == null || goalStep.CacheHandler?.TimeInMilliseconds == null) return;

			long time = goalStep.CacheHandler?.TimeInMilliseconds ?? 0;
			if (time == 0) return;

			var cacheKey = variableHelper.LoadVariables(goalStep.CacheHandler?.CacheKey)?.ToString();
			if (string.IsNullOrEmpty(cacheKey))
			{
				cacheKey = goalStep.CacheHandler?.CacheKey;
				if (string.IsNullOrEmpty(cacheKey)) return;
			}

			if (goalStep.CacheHandler?.Location == "disk")
			{
				string path;
				if (cacheKey.AdjustPathToOs().StartsWith(fileSystem.Path.DirectorySeparatorChar))
				{
					path = fileSystem.Path.Join(fileSystem.GoalsPath, cacheKey).AdjustPathToOs();
				}
				else
				{
					path = fileSystem.Path.Join(goal.AbsolutePrFolderPath, cacheKey).AdjustPathToOs();
				}

				var dirName = fileSystem.Path.GetDirectoryName(path);
				if (dirName != null && !fileSystem.Directory.Exists(dirName))
				{
					fileSystem.Directory.CreateDirectory(dirName);
				}
				if (fileSystem.File.Exists(path))
				{
					fileSystem.File.Delete(path);
				}
				fileSystem.File.WriteAllText(path, JsonConvert.SerializeObject(result));
			}
			else
			{
				if (goalStep.CacheHandler?.CachingType == 0)
				{
					await appCache.Set(cacheKey, result, TimeSpan.FromMilliseconds((double)time));
				}
				else
				{
					await appCache.Set(cacheKey, result, DateTime.Now.AddMilliseconds((double)time));
				}
			}
		}

		private async Task<bool> LoadCached(MethodInfo method, GenericFunction function)
		{
			if (goalStep?.CacheHandler == null || goalStep.CacheHandler?.CacheKey == null) return false;

			if (method.ReturnType == typeof(Task) || !goalStep.WaitForExecution)
			{
				if (!goalStep.WaitForExecution)
				{
					logger.LogWarning($"It is not possible to cache {method.Name} since it is not waiting for the execution.");
				}
				if (method.ReturnType == typeof(Task))
				{
					logger.LogWarning($"It is not possible to cache {method.Name} since it does not return value.");
				}
			}
			else
			{
				if (function.ReturnValues == null) return false;

				var cacheKey = variableHelper.LoadVariables(goalStep.CacheHandler?.CacheKey)?.ToString();
				if (string.IsNullOrEmpty(cacheKey))
				{
					cacheKey = goalStep.CacheHandler?.CacheKey;
					if (string.IsNullOrEmpty(cacheKey)) return false;
				}

				if (goalStep.CacheHandler?.Location == "disk")
				{
					string path;
					if (cacheKey.AdjustPathToOs().StartsWith(fileSystem.Path.DirectorySeparatorChar))
					{
						path = fileSystem.Path.Join(fileSystem.GoalsPath, cacheKey).AdjustPathToOs();
					}
					else
					{
						path = fileSystem.Path.Join(goal.AbsolutePrFolderPath, cacheKey).AdjustPathToOs();
					}

					if (!fileSystem.File.Exists(path)) return false;

					if (fileSystem.FileInfo.New(path).LastWriteTime < DateTime.Now.AddMilliseconds(-goalStep.CacheHandler.TimeInMilliseconds))
					{
						fileSystem.File.Delete(path);
						return false;
					}

					var txt = fileSystem.File.ReadAllText(path);
					var data = JsonConvert.DeserializeObject(txt);

					foreach (var returnValue in function.ReturnValues)
					{
						logger.LogDebug($"Cache was hit for {goalStep.CacheHandler.CacheKey}");
						memoryStack.Put(returnValue.VariableName, data);
					}
					return true;

				}
				else
				{
					var obj = await appCache.Get(cacheKey);
					if (obj != null && function.ReturnValues != null && function.ReturnValues.Count > 0)
					{
						foreach (var returnValue in function.ReturnValues)
						{
							logger.LogDebug($"Cache was hit for {goalStep.CacheHandler.CacheKey}");
							memoryStack.Put(returnValue.VariableName, obj);
						}
						return true;
					}
				}
			}


			return false;
		}



		//TODO: should create a cleanup here to kill processes correctly
		protected void KeepAlive(object instance, string key)
		{
			var alives = AppContext.GetData("KeepAlive") as List<Alive>;
			if (alives == null) alives = new List<Alive>();

			var aliveType = alives.FirstOrDefault(p => p.Type == instance.GetType() && p.Key == key);
			if (aliveType == null)
			{
				aliveType = new Alive(instance.GetType(), key);
				alives.Add(aliveType);

				AppContext.SetData("KeepAlive", alives);
			}
		}

		public void RemoveKeepAlive(object instance, string key)
		{
			var alives = AppContext.GetData("KeepAlive") as List<Alive>;
			var aliveType = alives.FirstOrDefault(p => p.Type == instance.GetType() && p.Key == key);
			if (aliveType != null)
			{
				alives.Remove(aliveType);

				AppContext.SetData("KeepAlive", alives);
			}
		}

		protected void RegisterForPLangUserInjections(string type, string pathToDll, bool globalForWholeApp = false, string? environmentVariable = null, string? environmentVariableValue = null)
		{
			this.container.RegisterForPLangUserInjections(type, pathToDll, globalForWholeApp, environmentVariable, environmentVariableValue);
		}

		public virtual async Task<(string info, IError? error)> GetAdditionalAssistantErrorInfo()
		{
			return (string.Empty, null);
		}
		public virtual async Task<string> GetAdditionalSystemErrorInfo()
		{
			return "";
		}


		protected string GetPath(string path)
		{
			return PathHelper.GetPath(path, fileSystem, this.Goal);
		}

		protected async Task<(string?, IError?)> AssistWithError(string error, GoalStep step, GenericFunction function)
		{
			AppContext.TryGetSwitch("llmerror", out bool isEnabled);
			if (!isEnabled) return (null, null);

			string additionSystemErrorInfo = await GetAdditionalSystemErrorInfo();
			string system = @$"You are c# expert developer debugging an error that c# throws.
The user is programming in a programming language called Plang, a pseudo language, that is built on top of c#.

The user is not familiar with c# and does not understand it, he only understands Plang.

You job is to identify why an error occurred that user provides.
You will be provided with function information and the parameters used. 
You will get description of what the function should do.
{additionSystemErrorInfo}

Be straight to the point, point out the most obvious reason and how to fix in plang source code. 
Be Concise";

			var additionalAssistant = await GetAdditionalAssistantErrorInfo();
			if (additionalAssistant.error != null)
			{
				return (null, additionalAssistant.error);
			}
			string assistant = @$"
## plang source code ##
{step.Text}
## plang source code ##
## function info ##
{typeHelper.GetMethodsAsString(this.GetType(), function.FunctionName)}
## function info ##
" + additionalAssistant.info;

			try
			{
				var promptMesage = new List<LlmMessage>();
				promptMesage.Add(new LlmMessage("system", system));
				promptMesage.Add(new LlmMessage("assistant", assistant));
				promptMesage.Add(new LlmMessage("user", error));

				var llmRequest = new LlmRequest("AssistWithError", promptMesage);

				return await llmServiceFactory.CreateHandler().Query<string>(llmRequest);
			}
			catch
			{
				return (null, new Error("ErrorToConnect", "Could not connect to LLM service"));
			}


		}


		public IError? TaskHasError(Task<(IEngine, object? Variables, IError? error, IOutput output)> task)
		{

			if (task.Exception != null)
			{
				var exception = task.Exception.InnerException ?? task.Exception;
				return new Error(exception.Message, Exception: exception);
			}

			return task.Result.error;
		}

		public T GetProgramModule<T>() where T : BaseProgram
		{
			var program = container.GetInstance<T>(typeof(T).FullName);
			program.Init(container, goal, goalStep, instruction, HttpListenerContext);
			return program;
		}

		public T? GetVariable<T>(string? variableName = null) where T : class
		{
			if (goal == null) return null;
			return goal.GetVariable<T>(variableName);
		}

		public void RemoveVariable<T>(string? variableName = null) where T : class
		{
			if (goal == null) return;

			goal.RemoveVariable<T>(variableName);
		}

		public void AddVariable<T>(T? value, Func<Task>? func = null, string? variableName = null) {
			if (goal == null) return;	
			goal.AddVariable<T>(value, func, variableName);
		}
	}
}
