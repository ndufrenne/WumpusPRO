using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WumpusGame
{
    public class Room
    {
        public int Id { get; set; }
        public string Description { get; set; }
        public Dictionary<string, int> Exits { get; set; } = new Dictionary<string, int>();
        public List<string> Hazards { get; set; } = new List<string>();
    }

    public class Map
    {
        public Dictionary<int, Room> Rooms { get; set; } = new Dictionary<int, Room>();
    }

    public class Player
    {
        public string Name { get; set; }
        public int CurrentRoomId { get; set; }
        public int Arrows { get; set; } = 3;
        public int Health { get; set; } = 100;
        public List<string> Inventory { get; set; } = new List<string>();
    }

    public class GameState
    {
        public Map Map { get; set; } = new Map();
        public List<Player> Players { get; set; } = new List<Player>();
        public int WumpusLocation { get; set; }
        public HashSet<int> Pits { get; set; } = new HashSet<int>();
    }

    public class Game
    {
        private GameState _state;
        private readonly HttpClient _httpClient = new HttpClient();
        private string OpenAiKey => Environment.GetEnvironmentVariable("OPENAI_KEY")
                                 ?? throw new Exception("Missing OPENAI_KEY environment variable");

        public async Task StartGame()
        {
            Console.WriteLine("Welcome to Hunt the Wumpus!");
            Console.Write("Enter your name: ");
            var name = Console.ReadLine();

            if (File.Exists("save.json") && Prompt("Load game? (y/n): "))
            {
                LoadGame();
            }
            else
            {
                InitializeNewGame(name);
            }

            await GameLoop();
        }

        private void InitializeNewGame(string playerName)
        {
            _state = new GameState
            {
                Map = new Map
                {
                    Rooms = new Dictionary<int, Room>
                    {
                        [1] = new Room
                        {
                            Id = 1,
                            Description = "You're in a dark cave. Exits: north, east",
                            Exits = new Dictionary<string, int> { ["north"] = 2, ["east"] = 3 }
                        },
                        [2] = new Room
                        {
                            Id = 2,
                            Description = "Musty chamber. Exits: south, east",
                            Exits = new Dictionary<string, int> { ["south"] = 1, ["east"] = 4 }
                        },
                        [3] = new Room
                        {
                            Id = 3,
                            Description = "Damp tunnel. Exits: west, north",
                            Exits = new Dictionary<string, int> { ["west"] = 1, ["north"] = 4 }
                        },
                        [4] = new Room
                        {
                            Id = 4,
                            Description = "High ledge with a pit! Exit: south",
                            Exits = new Dictionary<string, int> { ["south"] = 3 },
                            Hazards = new List<string> { "pit" }
                        }
                    }
                },
                Players = new List<Player> { new Player { Name = playerName, CurrentRoomId = 1 } },
                WumpusLocation = 2,
                Pits = new HashSet<int> { 4 }
            };
        }

        private async Task GameLoop()
        {
            var player = _state.Players[0];
            while (true)
            {
                var currentRoom = _state.Map.Rooms[player.CurrentRoomId];
                Console.WriteLine($"\n{currentRoom.Description}");
                PrintWarnings(player.CurrentRoomId);
                Console.WriteLine($"Exits: {string.Join(", ", currentRoom.Exits.Keys)}");
                Console.WriteLine($"Arrows: {player.Arrows}");

                Console.Write("\nWhat do you do? (move/shoot/ask/play/save/quit): ");
                var action = Console.ReadLine()?.ToLower();

                switch (action)
                {
                    case "quit": return;
                    case "save": SaveGame(); break;
                    case "move": HandleMove(player); break;
                    case "shoot": HandleShoot(player); break;
                    case "ask": await AskAI(currentRoom); break;
                    case "play": await PlayWithAI(); break;
                    default: Console.WriteLine("Invalid action"); break;
                }

                if (CheckEndCondition(player)) break;
            }
        }

        private async Task PlayWithAI()
        {
            Console.WriteLine("\nAI Companion joined your adventure!");
            var player = _state.Players[0];

            while (true)
            {
                var currentRoom = _state.Map.Rooms[player.CurrentRoomId];
                var prompt = $@"You are playing 'Hunt the Wumpus' with a human player. 
Current room: {currentRoom.Description}
Exits: {string.Join(", ", currentRoom.Exits.Keys)}
Wumpus location: {_state.WumpusLocation}
Player health: {player.Health}, arrows: {player.Arrows}

The player can: move [direction], shoot [direction], or ask for help.
Provide a short, fun response suggesting what to do next:";

                try
                {
                    var response = await GetAIResponse(prompt);
                    Console.WriteLine($"\nAI Companion says: {response}");

                    Console.Write("\nYour action (or 'stop' to end AI help): ");
                    var action = Console.ReadLine()?.ToLower();
                    if (action == "stop") break;

                    await ExecuteAICommand(action, player);

                    if (CheckEndCondition(player)) break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"AI Error: {ex.Message}");
                    break;
                }
            }
        }

        private async Task<string> GetAIResponse(string prompt)
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", OpenAiKey);

            var response = await _httpClient.PostAsync(
                "https://api.openai.com/v1/chat/completions",
                new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        model = "gpt-3.5-turbo",
                        messages = new[] { new { role = "user", content = prompt } },
                        max_tokens = 150,
                        temperature = 0.7
                    }),
                    Encoding.UTF8,
                    "application/json"));

            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            return result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        }

        private async Task ExecuteAICommand(string command, Player player)
        {
            var prompt = $@"In Hunt the Wumpus, execute this command: '{command}'
Current room ID: {player.CurrentRoomId}
Game state: {JsonSerializer.Serialize(_state)}

Respond ONLY with JSON containing:
{{
    ""action"": ""move"", ""shoot"", or ""invalid"",
    ""direction"": ""north"", ""south"", etc (if applicable),
    ""message"": ""Result description to show player""
}}";

            var response = await GetAIResponse(prompt);
            try
            {
                var action = JsonSerializer.Deserialize<AIAction>(response);
                Console.WriteLine(action.Message);

                if (action.Action == "move" && action.Direction != null)
                {
                    HandleMove(player, action.Direction);
                }
                else if (action.Action == "shoot" && action.Direction != null)
                {
                    HandleShoot(player, action.Direction);
                }
            }
            catch
            {
                Console.WriteLine("AI response was invalid. Try again.");
            }
        }

        private record AIAction(string Action, string Direction, string Message);

        private void HandleMove(Player player, string direction)
        {
            var currentRoom = _state.Map.Rooms[player.CurrentRoomId];

            if (currentRoom.Exits.TryGetValue(direction, out int newRoomId))
            {
                var newRoom = _state.Map.Rooms[newRoomId];
                if (newRoom.Hazards.Contains("pit"))
                {
                    Console.WriteLine("You fell into a pit! Game Over.");
                    Environment.Exit(0);
                }
                if (newRoomId == _state.WumpusLocation)
                {
                    Console.WriteLine("You walked into the Wumpus! Game Over.");
                    Environment.Exit(0);
                }
                player.CurrentRoomId = newRoomId;
                MoveWumpus();
            }
            else
            {
                Console.WriteLine("Invalid direction!");
            }
        }

        private void HandleMove(Player player)
        {
            Console.Write("Direction: ");
            var direction = Console.ReadLine()?.ToLower();
            HandleMove(player, direction);
        }

        private void HandleShoot(Player player, string direction)
        {
            if (player.Arrows <= 0)
            {
                Console.WriteLine("Out of arrows!");
                return;
            }

            var currentRoom = _state.Map.Rooms[player.CurrentRoomId];

            if (currentRoom.Exits.TryGetValue(direction, out int targetRoomId))
            {
                player.Arrows--;
                if (targetRoomId == _state.WumpusLocation)
                {
                    Console.WriteLine("You killed the Wumpus! You win!");
                    Environment.Exit(0);
                }
                else
                {
                    Console.WriteLine("Missed! The Wumpus growls nearby...");
                    MoveWumpus();
                }
            }
            else
            {
                Console.WriteLine("Invalid direction!");
            }
        }

        private void HandleShoot(Player player)
        {
            Console.Write("Direction: ");
            var direction = Console.ReadLine()?.ToLower();
            HandleShoot(player, direction);
        }

        private void MoveWumpus()
        {
            var current = _state.WumpusLocation;
            var exits = _state.Map.Rooms[current].Exits.Values;
            if (exits.Count > 0)
            {
                var exitList = new List<int>(exits);
                _state.WumpusLocation = exitList[new Random().Next(exitList.Count)];
            }
        }

        private void PrintWarnings(int roomId)
        {
            foreach (var adjRoomId in _state.Map.Rooms[roomId].Exits.Values)
            {
                if (adjRoomId == _state.WumpusLocation) Console.WriteLine("You smell a terrible stench!");
                if (_state.Pits.Contains(adjRoomId)) Console.WriteLine("You feel a breeze!");
            }
        }

        private async Task AskAI(Room currentRoom)
        {
            var prompt = $"In a text game, player is in: {currentRoom.Description}. Possible exits: {string.Join(", ", currentRoom.Exits.Keys)}. What should they do?";

            try
            {
                var response = await GetAIResponse(prompt);
                Console.WriteLine($"AI Advice: {response}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AI Error: {ex.Message}");
            }
        }

        private void SaveGame()
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText("save.json", JsonSerializer.Serialize(_state, options));
            Console.WriteLine("Game saved!");
        }

        private void LoadGame()
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var json = File.ReadAllText("save.json");
            _state = JsonSerializer.Deserialize<GameState>(json, options);
        }

        private bool CheckEndCondition(Player player)
        {
            return false;
        }

        private static bool Prompt(string message)
        {
            Console.Write(message);
            return Console.ReadLine()?.Trim().ToLower() == "y";
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                await new Game().StartGame();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine("Make sure you've set the OPENAI_KEY environment variable");
            }
        }
    }
}