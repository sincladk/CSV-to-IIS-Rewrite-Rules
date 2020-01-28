using CsvToIisRewriteRules.Models;
using Microsoft.VisualBasic.FileIO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CsvToIisRewriteRules
{
	public class Program
	{
		public static void Main(string[] args)
		{
			ExecutionState context = ParseArguments(args);

			if (string.IsNullOrEmpty(context.CsvPath) || string.IsNullOrEmpty(context.OutputDirectory))
			{
				Write("CsvToIisRewriteRules.exe -p <csv-file-path> -o <output-directory> [-s] [-a <destination-url>]");
			}
			else
			{
				if (!File.Exists(context.CsvPath))
				{
					WriteError($"Passed in CSV file path `{context.CsvPath}` does not exist (or you don't have access).");
					return;
				}

				if (!Directory.Exists(context.OutputDirectory))
				{
					WriteError($"Passed in output directory `{context.OutputDirectory}` does not exist (or you don't have access).");
					return;
				}

				var redirectMap = new Dictionary<string, Dictionary<string, string>>();

				redirectMap = ParseRedirectCsv(context, redirectMap);

				if (redirectMap.Count > 0)
				{
					var rewriteMapsElement = new XElement("rewriteMaps");
					var rulesElement = new XElement("rules");

					foreach (string sourceDomain in redirectMap.Keys)
					{
						Dictionary<string, string> redirects = redirectMap[sourceDomain];
						if (redirects.Count > 0)
						{
							XElement ruleElement;
							if (redirects.Count == 1)
							{
								KeyValuePair<string, string> redirect = redirects.First();
								string redirectSource = redirect.Key;
								if (string.IsNullOrEmpty(redirectSource) || redirectSource.Equals("/"))
								{
									// If the only redirect for this domain is the root path, put in a broad rule to handle all URL paths on this domain
									redirectSource = ".*";
								}
								else
								{
									redirectSource = Regex.Escape(redirectSource.TrimStart('/'));
								}

								ruleElement = XElement.Parse($"<rule name=\"Redirect rule for {sourceDomain}\" stopProcessing=\"true\"><match url=\"{redirectSource}\" /><conditions><add input=\"{{HTTP_HOST}}\" pattern=\"^{Regex.Escape(sourceDomain)}\" /></conditions><action type=\"Redirect\" url=\"{redirect.Value}\" appendQueryString=\"false\" /></rule>");
							}
							else
							{
								var rewriteMapElement = new XElement("rewriteMap", new XAttribute("name", $"{sourceDomain} map"));
								foreach (KeyValuePair<string, string> redirect in redirects)
								{
									rewriteMapElement.Add(new XElement("add", new XAttribute("key", !string.IsNullOrEmpty(redirect.Key) ? redirect.Key : "/"), new XAttribute("value", redirect.Value)));
								}

								rewriteMapsElement.Add(rewriteMapElement);

								ruleElement = XElement.Parse($"<rule name=\"Rewrite map rule for {sourceDomain}\" stopProcessing=\"true\"><match url=\".*\" /><conditions><add input=\"{{HTTP_HOST}}\" pattern=\"^{Regex.Escape(sourceDomain)}\" /><add input=\"{{{sourceDomain} map:{{REQUEST_URI}}}}\" pattern=\"(.+)\" /></conditions><action type=\"Redirect\" url=\"{{C:1}}\" appendQueryString=\"false\" /></rule>");
							}

							rulesElement.Add(ruleElement);
						}
						else
						{
							Write($"No redirects were found for the domain {sourceDomain}.");
						}
					}

					if (rulesElement.HasElements)
					{
						if (!string.IsNullOrEmpty(context.CatchAllRedirectDestinationUrl))
						{
							rulesElement.Add(XElement.Parse($"<rule name=\"Catch-all redirect rule\" stopProcessing=\"true\"><match url=\".*\" /><action type=\"Redirect\" url=\"{context.CatchAllRedirectDestinationUrl}\" appendQueryString=\"false\" /></rule>"));
						}

						var outputFiles = new Dictionary<string, XElement>();
						XElement includedRulesElement;
						XElement includedRewriteMapsElement;
						if (context.SeparateConfigFiles)
						{
							outputFiles.Add("rules.config", rulesElement);
							outputFiles.Add("rewriteMaps.config", rewriteMapsElement);
							includedRewriteMapsElement = XElement.Parse("<rewriteMaps configSource=\"rewriteMaps.config\"/>");
							includedRulesElement = XElement.Parse("<rules configSource=\"rules.config\"/>");
						}
						else
						{
							includedRewriteMapsElement = rewriteMapsElement;
							includedRulesElement = rulesElement;
						}

						outputFiles.Add("rewrite.config",
						                new XElement("rewrite",
						                             includedRewriteMapsElement,
						                             includedRulesElement));

						foreach (KeyValuePair<string, XElement> outputFilePair in outputFiles)
						{
							string outputFilePath = $"{context.OutputDirectory}\\{outputFilePair.Key}";
							Write($"Writing {outputFilePath}");
							File.WriteAllText(outputFilePath, outputFilePair.Value.ToString());
						}
					}
				}
				else
				{
					WriteError("No redirects were found.");
				}
			}
		}

		protected static Dictionary<string, Dictionary<string, string>> ParseRedirectCsv(ExecutionState context, Dictionary<string, Dictionary<string, string>> redirectMap)
		{
			using (var parser = new TextFieldParser(context.CsvPath))
			{
				parser.TextFieldType = FieldType.Delimited;
				parser.SetDelimiters(",");
				while (!parser.EndOfData)
				{
					string[] fields = parser.ReadFields();
					Debug.Assert(fields != null, nameof(fields) + " != null");
					if (fields.Length < 2)
					{
						WriteError($"Line {parser.LineNumber} contains fewer than 2 columns: {string.Join(",", fields)}");
					}
					else
					{
						Match match = Regex.Match(fields[0], "^(?:http|https):\\/\\/(?<domain>.+?\\.[\\w]{2,3})(?<path>\\/(.*)?)?$", RegexOptions.IgnoreCase);
						if (match.Success)
						{
							string sourceDomain = match.Groups["domain"].Value;
							string sourcePath = match.Groups["path"].Value;
							string destinationUrl = fields[1];

							if (!string.IsNullOrEmpty(sourceDomain) && !string.IsNullOrEmpty(destinationUrl))
							{
								AddToDictionary(ref redirectMap, sourceDomain, sourcePath, destinationUrl);
							}
						}
					}
				}
			}

			return redirectMap;
		}

		private static ExecutionState ParseArguments(string[] args)
		{
			var result = new ExecutionState();

			var index = 0;
			while (index < args.Length)
			{
				if (args[index].Equals("-s", StringComparison.OrdinalIgnoreCase))
				{
					result.SeparateConfigFiles = true;
				}
				else if (args[index].Equals("-a", StringComparison.OrdinalIgnoreCase))
				{
					index++;
					if (index > args.Length || args[index].StartsWith("-"))
					{
						WriteError("A destination URL was not specified after the -a switch");
						break;
					}

					result.CatchAllRedirectDestinationUrl = args[index];
				}
				else if (args[index].Equals("-p", StringComparison.OrdinalIgnoreCase))
				{
					index++;
					if (index > args.Length || args[index].StartsWith("-"))
					{
						WriteError("A CSV path was not specified after the -p switch");
						break;
					}

					result.CsvPath = args[index];
				}
				else if (args[index].Equals("-o", StringComparison.OrdinalIgnoreCase))
				{
					index++;
					if (index > args.Length || args[index].StartsWith("-"))
					{
						WriteError("An output directory was not specified after the -o switch");
						break;
					}

					result.OutputDirectory = args[index];
				}

				index++;
			}


			return result;
		}

		private static void AddToDictionary(ref Dictionary<string, Dictionary<string, string>> dictionary, string sourceDomain, string sourcePath, string destinationUrl)
		{
			if (!dictionary.TryGetValue(sourceDomain.ToLowerInvariant(), out Dictionary<string, string> redirects))
			{
				redirects = new Dictionary<string, string>();
			}

			redirects[sourcePath] = destinationUrl;

			dictionary[sourceDomain.ToLowerInvariant()] = redirects;
		}

		private static void WriteError(string message)
		{
			Write($"ERROR: {message}");
		}

		private static void Write(string message)
		{
			Console.WriteLine(message);
			Debug.WriteLine(message);
		}
	}
}