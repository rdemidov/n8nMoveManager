using System.Text.Json;
using Application;
using Application.Contracts;
using Application.Models;
namespace Infrastructure;
public sealed class WorkflowHealthService(IEnvironmentService environments, IEnvironmentN8nApiConfigStore configs, IHttpClientFactory clients) : IWorkflowHealthService
{
 public async Task<WorkflowHealthResult> GetAsync(string environmentKey, CancellationToken ct)
 {
  var env=(await environments.GetByKeyAsync(environmentKey,ct)).Environment; var c=await configs.GetAsync(env.Key,ct); var key=await configs.GetApiKeyAsync(env.Key,ct);
  if(!c.Enabled||string.IsNullOrWhiteSpace(c.BaseUrl)||string.IsNullOrWhiteSpace(key)) throw new WorkflowImportException("Configure the n8n API connection before viewing workflow health.");
  using var req=new HttpRequestMessage(HttpMethod.Get,new Uri(c.BaseUrl.TrimEnd('/')+"/api/v1/executions?status=error&limit=25")); req.Headers.Add("X-N8N-API-KEY",key);
  using var res=await clients.CreateClient("n8n").SendAsync(req,ct); var body=await res.Content.ReadAsStringAsync(ct); if(!res.IsSuccessStatusCode) throw new WorkflowImportException($"n8n executions API failed ({(int)res.StatusCode}): {body[..Math.Min(500,body.Length)]}");
  using var json=JsonDocument.Parse(body); var data=json.RootElement.TryGetProperty("data",out var d)&&d.ValueKind==JsonValueKind.Array?d.EnumerateArray():[];
  var rows=data.Select(x=>new WorkflowHealthItem(x.TryGetProperty("id",out var id)?id.ToString():"unknown",x.TryGetProperty("workflowId",out var w)?w.ToString():null,x.TryGetProperty("workflowName",out var n)?n.GetString():null,x.TryGetProperty("status",out var s)?s.GetString()??"error":"error",Date(x,"startedAt"),Date(x,"stoppedAt"))).ToArray(); return new(env.Key,rows);
 }
 static DateTimeOffset? Date(JsonElement x,string p)=>x.TryGetProperty(p,out var v)&&DateTimeOffset.TryParse(v.GetString(),out var d)?d:null;
}
