namespace CsvToIisRewriteRules.Models
{
	public class ExecutionState
	{
		public string CsvPath { get; set; }
		public string OutputDirectory { get; set; }
		public bool SeparateConfigFiles { get; set; }
		public string CatchAllRedirectDestinationUrl { get; set; }
	}
}