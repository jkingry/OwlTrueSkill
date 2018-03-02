using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace owstats
{
    class StageData
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public MatchData[] Matches { get; set; }
    }

    class MatchData
    {
        public int Id { get; set; }
        public ScoreData[] Scores { get; set; }
        public CompetitorData[] Competitors { get; set; }
        public long StartDateTS { get; set; }
        public string State { get; set; }
    }

    class ScoreData
    {
        public int Value { get; set; }
    }

    class CompetitorData
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    class Program
    {
        static void Main(string[] args)
        {

            var scheduleText = GetUrl("schedule", "https://api.overwatchleague.com/schedule").Result;
            JObject response = JObject.Parse(scheduleText);
            var stages = response["data"]["stages"].ToObject<StageData[]>();

            var results = from stage in stages
                          from match in stage.Matches
                          where match.State == "CONCLUDED"
                          orderby match.StartDateTS
                          let matchText = GetUrl("match." + match.Id, $"https://api.overwatchleague.com/match/{match.Id}").Result
                          select new
                          {
                              Stage = stage.Name,
                              Score = $"{match.Scores[0].Value} to {match.Scores[1].Value}",
                              Winner = match.Scores[0].Value > match.Scores[1].Value 
                                ? match.Competitors[0].Name
                                : match.Competitors[1].Name,
                              Loser = match.Scores[0].Value > match.Scores[1].Value
                                ? match.Competitors[1].Name
                                : match.Competitors[0].Name
                          };

            foreach(var r in results.Take(1))
            {
                Console.WriteLine(r);
            }

            Console.ReadKey();
        }

        static async Task<string> GetUrl(string name, string url)
        {
            var cache = name + ".cache";
            if (File.Exists(cache))
            {
                return File.ReadAllText(cache);
            }

            HttpClient client = new HttpClient();
            var result = await client.GetStringAsync(url);
            File.WriteAllText(cache, result);

            return result;
        }
    }
}
