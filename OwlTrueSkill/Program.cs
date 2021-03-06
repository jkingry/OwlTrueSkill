﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Moserware.Skills;
using Moserware.Skills.TrueSkill;
using Newtonsoft.Json.Linq;

namespace OwlTrueSkill
{
    class Program
    {
        static void Main(string[] args)
        {
            var redditText = args.Contains("reddit");
            var debug = args.Contains("debug");
            var stageNames = args.FirstOrDefault(a => a.StartsWith("stage="))?.Split('=')[1].Split(',');

            var scheduleText = GetUrl("schedule", "https://api.overwatchleague.com/schedule").Result;
            JObject response = JObject.Parse(scheduleText);
            var stages = response["data"]["stages"].ToObject<StageData[]>();

            var results = from stage in stages
                          where stageNames == null || stageNames.Any(x => x.Equals(stage.Name, StringComparison.OrdinalIgnoreCase))
                          from match in stage.Matches
                          where match.State == "CONCLUDED"
                          orderby match.StartDateTS
                          let matchText = GetUrl("match." + match.Id, $"https://api.overwatchleague.com/match/{match.Id}").Result
                          let matchResponse = JObject.Parse(matchText)
                          let games = matchResponse["games"].ToObject<GameData[]>()
                          from game in games
                          where game.State == "CONCLUDED"
                          select new
                          {
                              stage,
                              match,
                              game
                          };

            var calculator = new TwoTeamTrueSkillCalculator();

            var initialMean = 500.0;
            var beta = initialMean / 6.0;
            var drawProbability = 0.02;
            var dynamicsFactor = initialMean / 300.0;
            var standardDev = initialMean / 3.0;

            var gameInfo = new GameInfo(initialMean, standardDev, beta, dynamicsFactor, drawProbability);

            var ratings = new Dictionary<int, Rating>();
            var playerDb = new Dictionary<int, PlayerData>();
            var statDb = new Dictionary<int, PlayerStat>();
            var teamDb = new Dictionary<int, CompetitorData>();
            var gameCount = 0;
            var drawCount = 0;

            foreach(var r in results)
            {
                gameCount += 1;

                var team1 = new Team();
                var team2 = new Team();

                var team1Id = r.match.Competitors[0].Id;
                var team2Id = r.match.Competitors[1].Id;

                foreach(var team in r.match.Competitors)
                {
                    if (!teamDb.ContainsKey(team.Id))
                    {
                        teamDb[team.Id] = team;
                    }
                }

                foreach(var p in r.game.Players)
                {
                    if (!playerDb.ContainsKey(p.Player.Id))
                    {
                        playerDb[p.Player.Id] = p;
                        statDb[p.Player.Id] = new PlayerStat();
                    }

                    if (!ratings.TryGetValue(p.Player.Id, out var rating))
                    {
                        ratings[p.Player.Id] = rating = gameInfo.DefaultRating;
                    }

                    var team = p.Team.Id == r.match.Competitors[0].Id
                        ? team1
                        : p.Team.Id == r.match.Competitors[1].Id
                            ? team2
                            : null;

                    if (team != null)
                    {
                        team.AddPlayer(new Player(p.Player.Id), rating);
                    }
                }

                Debug.Assert(team1.AsDictionary().Count == 6);
                Debug.Assert(team2.AsDictionary().Count == 6);

                void Update(Team t, Action<PlayerStat> a)
                {
                    foreach(var k in t.AsDictionary().Keys.Select(k => (int)k.Id))
                    {
                        a(statDb[k]);
                    }
                }


                var team1Points = 1;
                var team2Points = 1;

                var result = "";
                if (r.game.Points[0] < r.game.Points[1])
                {
                    team1Points = 2;
                    result = $"WINNER {r.match.Competitors[1].Name}";
                    Update(team2, s => s.Win++);
                    Update(team1, s => s.Lose++);
                }
                else if (r.game.Points[1] < r.game.Points[0])
                {
                    team2Points = 2;
                    result = $"WINNER {r.match.Competitors[0].Name}";
                    Update(team1, s => s.Win++);
                    Update(team2, s => s.Lose++);
                }
                else
                {
                    drawCount += 1;
                    result = "DRAW";
                    Update(team1, s => s.Draw++);
                    Update(team2, s => s.Draw++);
                }

                var newRatings = calculator.CalculateNewRatings(gameInfo, Teams.Concat(team1, team2), team1Points, team2Points);

                if (debug) Console.WriteLine($"{r.match.Competitors[0].Name} vs {r.match.Competitors[1].Name} [{r.game.Points[0]} to {r.game.Points[1]}] {result}");

                foreach(var rating in newRatings)
                {
                    var pid = (int)rating.Key.Id;
                    var player = playerDb[pid];
                    var team = teamDb[player.Team.Id];
                    var oldRating = ratings[pid];
                    if (debug) Console.WriteLine($"  {team.Name} {player.Player.Name} : {oldRating.ConservativeRating} to {rating.Value.ConservativeRating}");

                    ratings[pid] = rating.Value;
                }
            }

            if (debug) Console.WriteLine("Finished");

            var rank = 0;

            IEnumerable<KeyValuePair<int, Rating>> ratingsList = ratings;

            var playerResults = from kv in ratingsList
                          let p = playerDb[kv.Key]
                          let t = teamDb[p.Team.Id]
                          let s = statDb[kv.Key]
                          select new
                          {
                              Team = t.Name,
                              Player = p.Player.Name,
                              Wins = s.Win,
                              Loses = s.Lose,
                              Draws = s.Draw,
                              Rating = kv.Value.ConservativeRating,
                              Detail = kv.Value
                          };

            if (redditText)
            {
                Console.WriteLine("Rank|Team|Player|Wins|Loses|Draws|Rating|Rating Detail");
                Console.WriteLine(":--|:--|:--|:-:|:-:|:-:|:-:|:--");
            }

            foreach (var r in playerResults.OrderByDescending(x => x.Rating))
            {
                rank += 1;
                if (redditText)
                {
                    Console.WriteLine($"{rank}|{r.Team}|{r.Player}|{r.Wins}|{r.Loses}|{r.Draws}|{r.Rating:f}|{r.Detail}");
                }
                else
                {
                    Console.WriteLine($"{rank,3} {r.Team,22} {r.Player,11} [{r.Wins,2}-{r.Loses,2}-{r.Draws,2}] {r.Rating,7:f} ({r.Detail})");
                }
            }
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

    class GameData
    {
        public int Id { get; set; }
        public int Number { get; set; }
        public int[] Points { get; set; }
        public string State { get; set; }
        public PlayerData[] Players { get; set; }
    }

    class PlayerTeamData
    {
        public int Id { get; set; }
    }
    class PlayerPlayerData
    {
        public int Id { get; set; }
        public string Handle { get; set; }
        public string Name { get; set; }
    }

    class PlayerData
    {
        public PlayerTeamData Team { get; set; }
        public PlayerPlayerData Player { get; set; }
    }

    class PlayerStat
    {
        public int Win, Lose, Draw;
        public int Games => Win + Lose + Draw;
    }
}
