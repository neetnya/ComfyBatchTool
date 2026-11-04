using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ComfyBatchTool.Services;

public class ComfyApiService
{
    private readonly HttpClient _http;
    public string BaseUrl { get; set; } = "http://127.0.0.1:8188";

    public ComfyApiService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task<string?> QueuePromptAsync(JsonObject workflow)
    {
        var payload = new { prompt = workflow };
        var content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{BaseUrl}/prompt", content);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"ComfyUI 返回 {resp.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("prompt_id", out var id)
            ? id.GetString() : null;
    }

    public async Task<bool> IsDoneAsync(string promptId)
    {
        var resp = await _http.GetAsync($"{BaseUrl}/history/{promptId}");
        if (!resp.IsSuccessStatusCode) return false;
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(promptId, out _);
    }
}