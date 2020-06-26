using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;
using CommandLine;

namespace learn_search
{
    class Program
    {
        // TODO: Add this to app config or read from an environment variable.
        static string RepoRoot = "/Users/thpetche/Dev/tpetchel/learn-pr";

        // Command-line options.
        public class Options
        {
            [Option('v', "verbose", Required = false, HelpText = "Print unit-level search results.")]
            public bool Verbose { get; set; }
            [Option('f', "file", Required = true, HelpText = "The .csv file to process.")]
            public string File { get; set; }
            [Option('t', "topic", Required = false, HelpText = "Process only this topic.")]
            public string Topic { get; set; }
        }

        // Represents a Learn unit (Markdown) file.
        class Unit
        {
            // Path on disk.
            public string Path { get; }

            // Link to parent.
            public Module Parent { get; set; }

            public Unit(string path)
            {
                Path = path;
                Parent = null;
            }

            // Gets the canonical URL to the unit.
            public string GetCanonicalUrl()
            {
                var file = System.IO.Path.GetFileNameWithoutExtension(Path);
                return $"{Parent.GetCanonicalUrl()}{file}/";
            }
        }

        // Represents a Learn module.
        class Module
        {
            // Path to module (directory) on disk.
            public string Path { get; }
            // Child units.
            public List<Unit> Units { get; }

            public Module(string path, IEnumerable<Unit> units)
            {
                Path = path;
                Units = new List<Unit>(units);
                // Set link to parent.
                foreach (var unit in Units)
                {
                    unit.Parent = this;
                }
            }

            // Cache the title, as reading it is an expensive operation.
            private string title = null;

            // Gets the module's title from metadata.
            public string GetTitle()
            {
                // Return the title if we already read it.
                if (title != null)
                {
                    return title;
                }

                // Provide a default title in case it can't be found.
                title = "<null>";
                // Read in index.yml and search for the 'title' field.
                string indexYml = System.IO.Path.Combine(Path, "index.yml");
                using (var reader = new StreamReader(indexYml))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        Regex regex = new Regex(@"^title:\s+(.+)");
                        Match match = regex.Match(line);
                        if (match.Success)
                        {
                            // Set tht title.
                            // Aggressively trim off whitespace and quotation marks.
                            title = match.Groups[1].Value.Trim().Trim('"').Trim('\'').Trim();
                            break;
                        }
                    }
                }
                return title;
            }

            // Gets the canonical URL to the module.
            public string GetCanonicalUrl()
            {
                var folder = new DirectoryInfo(Path).Name;
                return $"https://docs.microsoft.com/learn/modules/{folder}/";
            }

            // Determines whether the provided path refers to a Learn module directory.
            // We use this to distinguish unit files from shared snippet files.
            public static bool IsModule(string path)
            {
                return File.Exists(System.IO.Path.Combine(path, "index.yml"));
            }

            // Gets the path to the module directory from the given unit file.
            public static string GetModulePath(string markdownFile)
            {
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(markdownFile), ".."));
            }
        }

        // Describes search results.
        class SearchResults
        {
            // Aggregates hits within modules.
            public Dictionary<Module, int> ModuleHits { get; }
            // Tracks hits within unit (Markdown) files.
            public Dictionary<Unit, int> UnitHits { get; }

            public SearchResults()
            {
                ModuleHits = new Dictionary<Module, int>();
                UnitHits = new Dictionary<Unit, int>();
            }
        }

        // Describes a single search entry.
        class SearchEntry
        {
            // (Input) How much the search keyword factors in the overall results.
            public float Weight { get; }
            // (Input) A keyword (.NET regex) that the user is interested in discovering.
            public Regex RegexKeyword { get; }
            // (Output) Search results for this entry.
            public SearchResults Results { get; }

            public SearchEntry(float weight, string keyword)
            {
                Weight = weight;
                RegexKeyword = new Regex(keyword);
                Results = new SearchResults();
            }
        }

        static void Main(string[] args)
        {
            bool printRawHits = false;
            string csvFile = null;
            string topicFilter = null;
            Parser.Default.ParseArguments<Options>(args)
                .WithParsed<Options>(o =>
                {
                    printRawHits = o.Verbose;
                    csvFile = o.File;
                    topicFilter = o.Topic;
                });

            // Read search entries from file.
            // TODO: We can use a more robust .csv reader if we need to handle things like embedded commas.
            var topics = new Dictionary<string, List<SearchEntry>>();
            using(var reader = new StreamReader(csvFile))
            {
                int lineNumber = 1;
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    var values = line.Split(',');

                    var topic = values[0];

                    if (!topics.ContainsKey(topic))
                    {
                        topics.Add(topic, new List<SearchEntry>());
                    }
                    try
                    {
                        topics[topic].Add(new SearchEntry(float.Parse(values[1]), values[2]));
                    }
                    catch (FormatException)
                    {
                        Console.WriteLine($"!!Error processing topic '{topic}', line {lineNumber}.");
                    }
                    lineNumber++;
                }
            }

            // Visit each Markdown file in the repo and build out a temporary module structure.
            var scratchModules = new Dictionary<string, List<Unit>>();
            foreach (var markdownFile in Directory.GetFiles(RepoRoot, "*.md", SearchOption.AllDirectories))
            {
                var modulePath = Module.GetModulePath(markdownFile);
                if (Module.IsModule(modulePath))
                {
                    if (!scratchModules.ContainsKey(modulePath))
                    {
                        scratchModules.Add(modulePath, new List<Unit>());
                    }
                    scratchModules[modulePath].Add(new Unit(markdownFile));
                }
            }

            // Convert the raw collection to a list of Module objects.
            var modules = new List<Module>();
            foreach (var kvp in scratchModules)
            {
                modules.Add(new Module(kvp.Key, kvp.Value));
            }

            // Walk through each unit file in each module.
            // At each file, scan each line for search keywords.
            // When a match is found, add it to search results to process later.
            foreach (Module module in modules)
            {
                foreach (var unit in module.Units)
                {
                    using (StreamReader reader = new StreamReader(unit.Path))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            foreach (var topic in topics)
                            {
                                // Skip if the user specified a topic filter and this topic
                                // doesn't match it.
                                if (topicFilter != null && topicFilter != topic.Key)
                                {
                                    continue;
                                }

                                foreach (var entry in topic.Value)
                                {
                                    // Check whether the current keyword matches the current line.
                                    var matches = entry.RegexKeyword.Matches(line);

                                    // If there's a match, increment both the count against that unit
                                    // as well as aggregate the count for the parent module.
                                    if (matches.Count > 0)
                                    {
                                        var searchResults = entry.Results;
                                        if (searchResults.UnitHits.ContainsKey(unit))
                                        {
                                            searchResults.UnitHits[unit] += matches.Count;
                                        }
                                        else
                                        {
                                            searchResults.UnitHits.Add(unit, matches.Count);
                                        }

                                        if (searchResults.ModuleHits.ContainsKey(module))
                                        {
                                            searchResults.ModuleHits[module] += matches.Count;
                                        }
                                        else
                                        {
                                            searchResults.ModuleHits.Add(module, matches.Count);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // For each topic area, aggregate the weighted scores across all keywords.
            // This gives each module a score against the entire set of keywords for the current topic.
            // Recommend the top 3 results by printing info about them to the console.
            foreach (var topic in topics)
            {
                // Skip if the user specified a topic filter and this topic
                // doesn't match it.
                if (topicFilter != null && topicFilter != topic.Key)
                {
                    continue;
                }

                Console.WriteLine($"{topic.Key}");

                // Add up the weighted scores that were recorded for each module.
                // In other words, for:
                //
                // Topic T,
                // Keywords K1, K2, K3, ..., Kn,
                // Modules M1, M2, ..., Mm,
                //
                // We want to assign a weighted score, Sw, to each module
                // that represents how often the set of keywords appear in that module.
                var weightedScores = new Dictionary<Module, float>();
                // Walk each topic.
                foreach (var entry in topic.Value)
                {
                    // Walk each module that had at least one hit.
                    foreach (var moduleHit in entry.Results.ModuleHits)
                    {
                        // Compute the weighted score and add an entry to the collection.
                        var score = moduleHit.Value * entry.Weight;
                        var module = moduleHit.Key;
                        if (!weightedScores.ContainsKey(module))
                        {
                            weightedScores.Add(module, 0);
                        }
                        weightedScores[module] += score;
                    }
                }
                // Sort the scores from highest to lowest.
                var weightedScoresSorted = weightedScores.OrderByDescending(s => s.Value).ToList();
                // Take (at most) the top 3 modules and print info about each.
                int count = weightedScoresSorted.Count();
                if (count == 0)
                {
                    Console.WriteLine($"{Tabs(1)}No hits");
                }
                else
                {
                    if (count > 3) count = 3;
                    for (int i = 0; i < count; i++)
                    {
                        Console.WriteLine($"{Tabs(1)}Option {i+1}:");

                        var module = weightedScoresSorted[i].Key;
                        var score = weightedScoresSorted[i].Value;

                        Console.WriteLine($"{Tabs(2)}title: {module.GetTitle()}");
                        Console.WriteLine($"{Tabs(2)}score: {score}");
                        Console.WriteLine($"{Tabs(2)}url:   {module.GetCanonicalUrl()}");
                        Console.WriteLine($"{Tabs(2)}path:  {module.Path}");
                    }
                    // Print detailed information about the number of hits for each unit within
                    // the top 3 modules.
                    // This can help the user further interrogate each module.
                    if (printRawHits)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"{Tabs(1)}Raw hits:");
                        var topModules = new List<Module>();
                        for (int i = 0; i < count; i++)
                        {
                            topModules.Add(weightedScoresSorted[i].Key);
                        }
                        foreach (var entry in topic.Value)
                        {
                            Console.WriteLine($"{Tabs(2)}Keyword '{entry.RegexKeyword}' (weight={entry.Weight}):");
                            foreach (var topModule in topModules)
                            {
                                foreach (var unitHit in entry.Results.UnitHits)
                                {
                                    var unit = unitHit.Key;
                                    if (unit.Parent == topModule)
                                    {
                                        var hits = unitHit.Value;
                                        Console.WriteLine($"{Tabs(3)}{unit.GetCanonicalUrl()} ({hits} occurrences)");
                                    }
                                }
                            }
                        }
                    }
                }
                Console.WriteLine();
            }
        }

        // Helper for printing tabs.
        static string Tabs(int n)
        {
            // '\t' seemed extreme, so using 2 spaces per tab.
            return new String(' ', 2*n);
        }
    }
}
