// Minimal external bot for Xiangqi Arena: joins an open seat with kind "bot",
// then polls the room over HTTP and plays the first legal move it finds
// (replace PickMove with real search to make it strong).
//
//   dotnet run --project examples/ExternalBot -- http://127.0.0.1:5080 <roomId> black
//
// Create a room with an open black seat first:
//   curl -s http://127.0.0.1:5080/api/rooms -H 'Content-Type: application/json' \
//        -d '{"redKind":"human","blackKind":"open"}'
using System.Net.Http.Json;
using System.Text.Json;

string baseUrl = (args.Length > 0 ? args[0] : "http://127.0.0.1:5080").TrimEnd('/');
if (args.Length < 2)
{
    Console.WriteLine("usage: ExternalBot <baseUrl> <roomId> [red|black]");
    return 1;
}
string roomId = args[1];
string color = args.Length > 2 ? args[2] : "black";

using var http = new HttpClient { BaseAddress = new Uri(baseUrl + "/") };
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// 1. Take the seat; the token only authorizes this side.
var join = await http.PostAsJsonAsync($"api/rooms/{roomId}/join", new { color, kind = "bot" });
if (!join.IsSuccessStatusCode)
{
    Console.WriteLine($"join failed: {(int)join.StatusCode} {await join.Content.ReadAsStringAsync()}");
    return 1;
}
string token = (await join.Content.ReadFromJsonAsync<JsonElement>(json)).GetProperty("token").GetString()!;
Console.WriteLine($"joined {roomId} as {color}");

// 2. Poll; when it is our turn pick a move and POST it.
while (true)
{
    var state = await http.GetFromJsonAsync<JsonElement>($"api/rooms/{roomId}", json);
    string status = state.GetProperty("status").GetString()!;
    if (status != "ongoing")
    {
        Console.WriteLine($"game over: {status}");
        return 0;
    }
    if (state.GetProperty("sideToMove").GetString() == color)
    {
        var legal = await http.GetFromJsonAsync<string[]>($"api/rooms/{roomId}/moves", json) ?? Array.Empty<string>();
        if (legal.Length > 0)
        {
            string move = PickMove(legal);
            var res = await http.PostAsJsonAsync($"api/rooms/{roomId}/moves", new { move, token });
            Console.WriteLine($"{move} -> {(int)res.StatusCode}");
        }
    }
    await Task.Delay(700);
}

static string PickMove(string[] legal) => legal[Random.Shared.Next(legal.Length)];
