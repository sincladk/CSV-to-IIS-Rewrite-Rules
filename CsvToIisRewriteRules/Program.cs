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


				SortedDictionary<string, SortedDictionary<string, string>> redirectMap = ParseRedirectCsv(context);

				if (redirectMap.Count > 0)
				{
					var rewriteMapsElement = new XElement("rewriteMaps");
					var rulesElement = new XElement("rules");

					foreach (string sourceDomain in redirectMap.Keys)
					{
						SortedDictionary<string, string> redirects = redirectMap[sourceDomain];
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

								ruleElement = new XElement("rule", new XAttribute("name", $"Redirect rule for {sourceDomain}"), new XAttribute("stopProcessing", "true"),
								                           new XElement("match", new XAttribute("url", redirectSource)),
								                           new XElement("conditions",
								                                        new XElement("add", new XAttribute("input", "{HTTP_HOST}"), new XAttribute("pattern", $"^(www\\.)?{Regex.Escape(sourceDomain)}$"))
								                           ),
								                           new XElement("action", new XAttribute("type", "Redirect"), new XAttribute("url", redirect.Value), new XAttribute("appendQueryString", "false"))
								);
							}
							else
							{
								var rewriteMapElement = new XElement("rewriteMap", new XAttribute("name", $"{sourceDomain} map"));
								foreach (KeyValuePair<string, string> redirect in redirects)
								{
									rewriteMapElement.Add(new XElement("add", new XAttribute("key", !string.IsNullOrEmpty(redirect.Key) ? redirect.Key : "/"), new XAttribute("value", redirect.Value)));
								}

								rewriteMapsElement.Add(rewriteMapElement);

								ruleElement = new XElement("rule", new XAttribute("name", $"Rewrite map rule for {sourceDomain}"), new XAttribute("stopProcessing", "true"),
														   new XElement("match", new XAttribute("url", ".*")),
														   new XElement("conditions",
																		new XElement("add", new XAttribute("input", "{HTTP_HOST}"), new XAttribute("pattern", $"^(www\\.)?{Regex.Escape(sourceDomain)}$")),
																		new XElement("add", new XAttribute("input", $"{{{sourceDomain} map:{{REQUEST_URI}}}}"), new XAttribute("pattern", "(.+)"))
														   ),
														   new XElement("action", new XAttribute("type", "Redirect"), new XAttribute("url", "{C:1}"), new XAttribute("appendQueryString", "false"))
								);
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

		protected static SortedDictionary<string, SortedDictionary<string, string>> ParseRedirectCsv(ExecutionState context)
		{
			var redirectMap = new SortedDictionary<string, SortedDictionary<string, string>>();
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

							// Only add this redirect if the destination URL does not match the catch all URL (if it's been specified)
							if (!string.IsNullOrEmpty(sourceDomain) && !string.IsNullOrEmpty(destinationUrl) && !destinationUrl.Equals(context.CatchAllRedirectDestinationUrl))
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

		private static void AddToDictionary(ref SortedDictionary<string, SortedDictionary<string, string>> dictionary, string sourceDomain, string sourcePath, string destinationUrl)
		{
			string cleanSourceDomain = CleanSourceDomain(sourceDomain);
			if (!dictionary.TryGetValue(cleanSourceDomain, out SortedDictionary<string, string> redirects))
			{
				redirects = new SortedDictionary<string, string>();
			}

			string cleanSourcePath = sourcePath;
			if (string.IsNullOrEmpty(cleanSourcePath))
			{
				cleanSourcePath = "/";
			}
			else if (cleanSourcePath.Contains('?'))
			{
				cleanSourcePath = cleanSourcePath.Substring(0, cleanSourcePath.IndexOf('?'));
			}

			redirects[cleanSourcePath] = destinationUrl;

			dictionary[cleanSourceDomain] = redirects;
		}

		protected static string CleanSourceDomain(string sourceDomain)
		{
			if (sourceDomain.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && sourceDomain.Length > 4)
			{
				sourceDomain = sourceDomain.Substring(4);
			}

			return sourceDomain.ToLowerInvariant();
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