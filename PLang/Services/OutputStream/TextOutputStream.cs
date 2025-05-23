﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PLang.Errors;
using PLang.Utils;
using System;
using System.Net;
using System.Text;
using static PLang.Utils.StepHelper;

namespace PLang.Services.OutputStream
{
	public class TextOutputStream : IOutputStream, IDisposable
	{
		private readonly HttpListenerContext httpContext;
		private readonly MemoryStream memoryStream;
		public TextOutputStream(HttpListenerContext httpContext)
		{
			this.httpContext = httpContext;
			this.memoryStream = new MemoryStream();
		}

		public Stream Stream { get { return this.memoryStream; } }
		public Stream ErrorStream { get { return this.memoryStream; } }

		public string Output => "text";

		public async Task<string> Ask(string text, string type, int statusCode = 400, Dictionary<string, object>? parameters = null, Callback? callback = null)
		{
			throw new NotImplementedException();

			using (var writer = new StreamWriter(httpContext.Response.OutputStream, httpContext.Response.ContentEncoding ?? Encoding.UTF8))
			{
				if (text != null)
				{
					string content = text.ToString();
					

					await writer.WriteAsync(content);
				}
				await writer.FlushAsync();
			}
			return "";
		}

		public void Dispose()
		{
			memoryStream.Dispose();
		}

		public string Read()
		{
			return "";
		}
		private string? GetAsString(object? obj)
		{
			if (obj == null) return null;

			if (obj is JValue || obj is JObject || obj is JArray)
			{
				return obj.ToString();
			}
			if (obj is IError)
			{
				return ((IError)obj).ToFormat("json").ToString();
			}
			else
			{
				string content = obj.ToString()!;
				

				return content;
			}


		}
		public async Task Write(object? obj, string type, int httpStatusCode = 200, Dictionary<string, object?>? paramaters = null)
		{
			httpContext.Response.StatusCode = httpStatusCode;
			httpContext.Response.StatusDescription = type;

			string? content = GetAsString(obj);
			if (content == null) return;

			byte[] buffer = Encoding.UTF8.GetBytes(content);
			memoryStream.Write(buffer, 0, buffer.Length);
			//httpContext.Response.OutputStream.Write(buffer, 0, buffer.Length);

			
			return;
			
		}

		public async Task WriteToBuffer(object? obj, string type, int httpStatusCode = 200)
		{
			httpContext.Response.StatusCode = httpStatusCode;
			httpContext.Response.StatusDescription = type;
			httpContext.Response.SendChunked = true;

			string? content = GetAsString(obj);
			if (content == null) return;

			byte[] buffer = Encoding.UTF8.GetBytes(content);
			httpContext.Response.OutputStream.Write(buffer, 0, buffer.Length);
			

		}
	}
}
