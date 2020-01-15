using Microsoft.VisualBasic.FileIO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CsvToIisRewriteRules
{
	public class Program
	{
		public static void Main(string[] args)
		{
			if (args.Length < 2)
			{
				Write("Usage: CsvToIisRewriteRules.exe <csv-file-path> <output-directory> [-s]");
			}
			else
			{
				if (!File.Exists(args[0]))
				{
					WriteError($"Passed in CSV file path `{args[0]}` does not exist (or you don't have access).");
					return;
				}

				string csvFilePath = args[0];

				if (!Directory.Exists(args[1]))
				{
					WriteError($"Passed in output directory `{args[1]}` does not exist (or you don't have access).");
					return;
				}

				string outputDirectoryPath = args[1];

				bool separateOutputFiles = args.Length > 2 && args[2].StartsWith("-s", StringComparison.OrdinalIgnoreCase);

				using (var parser = new TextFieldParser(csvFilePath))
				{
					var redirectMap = new Dictionary<string, List<KeyValuePair<string, string>>>();

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

					if (redirectMap.Count > 0)
					{
						var rewriteMapsElement = new XElement("rewriteMaps");
						var rulesElement = new XElement("rules");
						var outputFiles = new Dictionary<string, XElement>();

						foreach (string sourceDomain in redirectMap.Keys)
						{
							List<KeyValuePair<string, string>> redirects = redirectMap[sourceDomain];
							if (redirects.Count > 0)
							{
								var rewriteMapElement = new XElement("rewriteMap", new XAttribute("name", $"{sourceDomain} map"));
								foreach (KeyValuePair<string, string> redirect in redirects)
								{
									rewriteMapElement.Add(new XElement("add", new XAttribute("key", !string.IsNullOrEmpty(redirect.Key) ? redirect.Key : "/"), new XAttribute("value", redirect.Value)));
								}

								rewriteMapsElement.Add(rewriteMapElement);

								XElement ruleElement = XElement.Parse($"<rule name=\"Rewrite map rule for {sourceDomain}\" stopProcessing=\"true\"><match url=\".*\" /><conditions><add input=\"{{HTTP_HOST}}\" pattern=\"^{Regex.Escape(sourceDomain)}\" /><add input=\"{{{sourceDomain} map:{{REQUEST_URI}}}}\" pattern=\"(.+)\" /></conditions><action type=\"Redirect\" url=\"{{C:1}}\" appendQueryString=\"false\" /></rule>");
								rulesElement.Add(ruleElement);
							}
							else
							{
								Write($"No redirects were found for the domain {sourceDomain}.");
							}
						}

						if (rewriteMapsElement.HasElements && rulesElement.HasElements)
						{
							XElement includedRulesElement;
							XElement includedRewriteMapsElement;
							if (separateOutputFiles)
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
								string outputFilePath = $"{outputDirectoryPath}\\{outputFilePair.Key}";
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
		}

		private static void AddToDictionary(ref Dictionary<string, List<KeyValuePair<string, string>>> dictionary, string sourceDomain, string sourcePath, string destinationUrl)
		{
			if (!dictionary.TryGetValue(sourceDomain.ToLowerInvariant(), out List<KeyValuePair<string, string>> redirects))
			{
				redirects = new List<KeyValuePair<string, string>>();
			}

			redirects.Add(new KeyValuePair<string, string>(sourcePath, destinationUrl));

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