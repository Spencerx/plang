﻿using PLang.Building.Model;

namespace PLang.Errors.Builder
{
	public record InstructionBuilderError(string Message, GoalStep Step, string Key = "InstructionBuilder", int StatusCode = 400,
		bool ContinueBuild = true, Exception? Exception = null, string? FixSuggestion = null, string? HelpfulLinks = null) : 
		StepBuilderError(Message, Step, Key, StatusCode, ContinueBuild, Exception, FixSuggestion, HelpfulLinks)
	{
		public override GoalStep Step { get; set; } = Step;
		public override Goal Goal { get; set; } = Step.Goal;
		public override string ToString()
		{
			return base.ToString();
		}
	}

}
