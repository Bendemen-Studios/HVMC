using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HVMCLauncher.CrossPlatform.Services;

public sealed class ContentSyncService
{
    private const string Repo="Bendemen-Studios/HVMC";
    private const string Branch="main";
    private const string MinecraftVersion="26.2";
    private const string FabricLoader="0.19.3";
    private readonly HttpClient _http; private readonly LauncherLogger _log;
    public string FabricProfile=>$"fabric-loader-{FabricLoader}-{MinecraftVersion}";

    public ContentSyncService(HttpClient http, LauncherLogger log){_http=http;_log=log;}

    public async Task SyncAsync(bool forceRedownload=false,CancellationToken ct=default)
    {
        HvmcPaths.Ensure();
        var index=await GetIndexAsync(ct);
        var state=await ReadJsonAsync(HvmcPaths.ContentState,ct);
        var manifest=await ReadJsonAsync(HvmcPaths.ContentManifest,ct);
        var old=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);

        if(!forceRedownload && manifest?.TryGetProperty("files",out var oldFiles)==true && oldFiles.ValueKind==JsonValueKind.Array)
            foreach(var entry in oldFiles.EnumerateArray())
                if(entry.TryGetProperty("path",out var p)&&entry.TryGetProperty("sha",out var s))
                    old[p.GetString()??""]=s.GetString()??"";

        if(!forceRedownload && state?.TryGetProperty("remoteTreeSha",out var tree)==true &&
           tree.GetString()==index.TreeSha && old.Count>0 &&
           File.Exists(Path.Combine(HvmcPaths.MinecraftRoot,"versions",FabricProfile,$"{FabricProfile}.json")))
        { _log.Info("HVMC content is al actueel."); return; }

        var current=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var remote=new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach(var file in index.Files)
        {
            var relative=SafeRelative(file.Path); remote.Add(relative); current[relative]=file.Sha;
            var destination=Path.Combine(HvmcPaths.MinecraftRoot,relative.Replace('/',Path.DirectorySeparatorChar));
            var manifestMatch=old.TryGetValue(relative,out var oldSha)&&oldSha==file.Sha;
            if(forceRedownload || !File.Exists(destination) || (!manifestMatch&&!VerifyGitBlobSha(destination,file.Sha)))
                await DownloadAsync(file.Download,destination,file.Sha,ct);
        }

        foreach(var path in old.Keys)
            if(!remote.Contains(path))
            {
                var obsolete=Path.Combine(HvmcPaths.MinecraftRoot,path.Replace('/',Path.DirectorySeparatorChar));
                if(File.Exists(obsolete)) File.Delete(obsolete);
            }

        var entries=current.OrderBy(x=>x.Key).Select(x=>new {path=x.Key,sha=x.Value}).ToArray();
        await File.WriteAllTextAsync(HvmcPaths.ContentManifest,JsonSerializer.Serialize(new {version=index.Version,files=entries,updated=DateTimeOffset.UtcNow}),ct);
        await File.WriteAllTextAsync(HvmcPaths.ContentState,JsonSerializer.Serialize(new {installedVersion=index.Version,remoteTreeSha=index.TreeSha,updated=DateTimeOffset.UtcNow}),ct);

        var fabric=Path.Combine(HvmcPaths.MinecraftRoot,"versions",FabricProfile,$"{FabricProfile}.json");
        if(!File.Exists(fabric)) throw new InvalidOperationException($"Gebundelde Fabric-installatie ontbreekt: content/versions/{FabricProfile}/{FabricProfile}.json");
        _log.Info("HVMC content + Fabric-runtime synchronisatie voltooid.");
    }

    private async Task<ContentIndex> GetIndexAsync(CancellationToken ct)
    {
        var url=$"https://raw.githubusercontent.com/{Repo}/{Branch}/content-index.json"; Exception? last=null;
        for(var attempt=1;attempt<=3;attempt++)
        {
            try
            {
                using var response=await _http.GetAsync(url,ct); response.EnsureSuccessStatusCode();
                var index=JsonSerializer.Deserialize<ContentIndex>(await response.Content.ReadAsStringAsync(ct),JsonOptions)
                    ?? throw new InvalidOperationException("GitHub content-index.json is ongeldig.");
                if(index.Files.Count==0) throw new InvalidOperationException("GitHub content-index.json bevat geen bestanden.");
                return index;
            }
            catch(Exception ex){last=ex;if(attempt<3)await Task.Delay(Math.Min(2*attempt,5)*1000,ct);}
        }
        throw new InvalidOperationException($"GitHub content-index kon niet worden geladen: {last?.Message}",last);
    }

    private async Task DownloadAsync(string url,string destination,string sha,CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!); var temp=destination+".download"; Exception? last=null;
        for(var attempt=1;attempt<=3;attempt++)
        {
            try
            {
                using var response=await _http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,ct); response.EnsureSuccessStatusCode();
                await using var source=await response.Content.ReadAsStreamAsync(ct); await using var target=File.Create(temp);
                await source.CopyToAsync(target,ct); await target.FlushAsync(ct);
                if(!VerifyGitBlobSha(temp,sha)) throw new InvalidOperationException($"SHA-controle mislukt voor {url}");
                File.Move(temp,destination,true); return;
            }
            catch(Exception ex){last=ex;try{if(File.Exists(temp))File.Delete(temp);}catch{}if(attempt<3)await Task.Delay(Math.Min(2*attempt,5)*1000,ct);}
        }
        throw new InvalidOperationException($"Download mislukt: {url}: {last?.Message}",last);
    }

    private static bool VerifyGitBlobSha(string path,string expected)
    {
        try
        {
            var bytes=File.ReadAllBytes(path); var header=Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
            var all=new byte[header.Length+bytes.Length]; Buffer.BlockCopy(header,0,all,0,header.Length); Buffer.BlockCopy(bytes,0,all,header.Length,bytes.Length);
            return Convert.ToHexString(SHA1.HashData(all)).Equals(expected,StringComparison.OrdinalIgnoreCase);
        }catch{return false;}
    }

    private static string SafeRelative(string path)
    {
        path=path.Replace('\\','/').TrimStart('/');
        if(!path.StartsWith("content/",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException($"Ongeldig content-pad: {path}");
        var relative=path["content/".Length..]; var parts=relative.Split('/',StringSplitOptions.RemoveEmptyEntries);
        if(parts.Any(p=>p=="."||p=="..")||Path.IsPathRooted(relative))throw new InvalidOperationException($"Onveilig content-pad: {relative}");
        return string.Join('/',parts);
    }

    private sealed record ContentIndex(string TreeSha,string Version,List<ContentFile> Files);
    private sealed record ContentFile(string Path,string Sha){public string Download=>$"https://raw.githubusercontent.com/{Repo}/{Branch}/{Path}";}
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNameCaseInsensitive=true};
    private static async Task<JsonElement?> ReadJsonAsync(string path,CancellationToken ct)
    {
        try{if(!File.Exists(path))return null;using var doc=JsonDocument.Parse(await File.ReadAllTextAsync(path,ct));return doc.RootElement.Clone();}catch{return null;}
    }
}