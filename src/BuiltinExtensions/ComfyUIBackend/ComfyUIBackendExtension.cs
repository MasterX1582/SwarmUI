﻿using FreneticUtilities.FreneticExtensions;
using FreneticUtilities.FreneticToolkit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using StableSwarmUI.Accounts;
using StableSwarmUI.Backends;
using StableSwarmUI.Core;
using StableSwarmUI.Text2Image;
using StableSwarmUI.Utils;
using StableSwarmUI.WebAPI;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Xml.Linq;

namespace StableSwarmUI.Builtin_ComfyUIBackend;

/// <summary>Main class for the ComfyUI Backend extension.</summary>
public class ComfyUIBackendExtension : Extension
{
    public static string Folder;

    public static Dictionary<string, string> Workflows;

    /// <summary>Set of all feature-ids supported by ComfyUI backends.</summary>
    public static HashSet<string> FeaturesSupported = new() { "comfyui", "refiners", "controlnet", "endstepsearly", "seamless" };

    /// <summary>Extensible map of ComfyUI Node IDs to supported feature IDs.</summary>
    public static Dictionary<string, string> NodeToFeatureMap = new()
    {
        ["SwarmLoadImageB64"] = "comfy_loadimage_b64",
        ["SwarmSaveImageWS"] = "comfy_saveimage_ws",
        ["SwarmLatentBlendMasked"] = "comfy_latent_blend_masked",
        ["SwarmKSampler"] = "variation_seed",
        ["FreeU"] = "freeu",
        ["AITemplateLoader"] = "aitemplate",
        ["IPAdapter"] = "ipadapter",
        ["IPAdapterApply"] = "ipadapter",
        ["IPAdapterModelLoader"] = "cubiqipadapter"
    };

    public override void OnPreInit()
    {
        Folder = FilePath;
        LoadWorkflowFiles();
        Program.ModelRefreshEvent += Refresh;
        ScriptFiles.Add("Assets/comfy_workflow_editor_helper.js");
        StyleSheetFiles.Add("Assets/comfy_workflow_editor.css");
        T2IParamTypes.FakeTypeProviders.Add(DynamicParamGenerator);
    }

    public override void OnShutdown()
    {
        T2IParamTypes.FakeTypeProviders.Remove(DynamicParamGenerator);
    }

    public static T2IParamType FakeRawInputType = new("comfyworkflowraw", "", "", Type: T2IParamDataType.TEXT, ID: "comfyworkflowraw", FeatureFlag: "comfyui", HideFromMetadata: true); // TODO: Setting to toggle metadata

    public T2IParamType DynamicParamGenerator(string name, T2IParamInput context)
    {
        if (name == "comfyworkflowraw")
        {
            return FakeRawInputType;
        }
        else if (name.StartsWith("comfyrawworkflowinput") && (context.ValuesInput.ContainsKey("comfyworkflowraw") || context.ValuesInput.ContainsKey("comfyuicustomworkflow")))
        {
            string nameNoPrefix = name.After("comfyrawworkflowinput");
            T2IParamDataType type = FakeRawInputType.Type;
            ParamViewType numberType = ParamViewType.BIG;
            if (nameNoPrefix.StartsWith("seed"))
            {
                type = T2IParamDataType.INTEGER;
                numberType = ParamViewType.SEED;
                nameNoPrefix = nameNoPrefix.After("seed");
            }
            else
            {
                foreach (T2IParamDataType possible in Enum.GetValues<T2IParamDataType>())
                {
                    string typeId = possible.ToString().ToLowerFast();
                    if (nameNoPrefix.StartsWith(typeId))
                    {
                        nameNoPrefix = nameNoPrefix.After(typeId);
                        type = possible;
                        break;
                    }
                }
            }
            T2IParamType resType = FakeRawInputType with { Name = nameNoPrefix, ID = name, HideFromMetadata = false, Type = type, ViewType = numberType };
            if (type == T2IParamDataType.MODEL)
            {
                static string cleanup(string _, string val)
                {
                    val = val.Replace('\\', '/');
                    while (val.Contains("//"))
                    {
                        val = val.Replace("//", "/");
                    }
                    val = val.Replace('/', Path.DirectorySeparatorChar);
                    return val;
                }
                resType = resType with { Clean = cleanup };
            }
            return resType;
        }
        return null;
    }

    public static IEnumerable<ComfyUIAPIAbstractBackend> RunningComfyBackends => Program.Backends.RunningBackendsOfType<ComfyUIAPIAbstractBackend>();

    public void LoadWorkflowFiles()
    {
        Workflows = new();
        foreach (string workflow in Directory.EnumerateFiles($"{Folder}/Workflows", "*.json", new EnumerationOptions() { RecurseSubdirectories = true }).Order())
        {
            string fileName = workflow.Replace('\\', '/').After("/Workflows/");
            if (fileName.EndsWith(".json"))
            {
                Workflows.Add(fileName.BeforeLast('.'), File.ReadAllText(workflow));
            }
        }
        CustomWorkflows.Clear();
        if (Directory.Exists($"{Folder}/CustomWorkflows"))
        {
            foreach (string workflow in Directory.EnumerateFiles($"{Folder}/CustomWorkflows", "*.json", new EnumerationOptions() { RecurseSubdirectories = true }).Order())
            {
                string fileName = workflow.Replace('\\', '/').After("/CustomWorkflows/");
                if (fileName.EndsWith(".json"))
                {
                    string name = fileName.BeforeLast('.');
                    CustomWorkflows.TryAdd(name, name);
                }
            }
        }
    }

    public void Refresh()
    {
        LoadWorkflowFiles();
        List<Task> tasks = new();
        foreach (ComfyUIAPIAbstractBackend backend in RunningComfyBackends.ToArray())
        {
            tasks.Add(backend.LoadValueSet());
        }
        if (tasks.Any())
        {
            Task.WaitAll(tasks.ToArray(), Program.GlobalProgramCancel);
        }
    }


    public static void AssignValuesFromRaw(JObject rawObjectInfo)
    {
        lock (ValueAssignmentLocker)
        {
            if (rawObjectInfo.TryGetValue("UpscaleModelLoader", out JToken modelLoader))
            {
                UpscalerModels = UpscalerModels.Concat(modelLoader["input"]["required"]["model_name"][0].Select(u => $"model-{u}")).Distinct().ToList();
            }
            if (rawObjectInfo.TryGetValue("KSampler", out JToken ksampler))
            {
                Samplers = Samplers.Concat(ksampler["input"]["required"]["sampler_name"][0].Select(u => $"{u}")).Distinct().ToList();
                Schedulers = Schedulers.Concat(ksampler["input"]["required"]["scheduler"][0].Select(u => $"{u}")).Distinct().ToList();
            }
            if (rawObjectInfo.TryGetValue("IPAdapter", out JToken ipadapter))
            {
                IPAdapterModels = IPAdapterModels.Concat(ipadapter["input"]["required"]["model_name"][0].Select(m => $"{m}")).Distinct().ToList();
            }
            if (rawObjectInfo.TryGetValue("IPAdapterModelLoader", out JToken ipadapterCubiq))
            {
                IPAdapterModels = IPAdapterModels.Concat(ipadapterCubiq["input"]["required"]["ipadapter_file"][0].Select(m => $"{m}")).Distinct().ToList();
            }
            foreach ((string key, JToken data) in rawObjectInfo)
            {
                if (data["category"].ToString() == "image/preprocessors")
                {
                    ControlNetPreprocessors[key] = data;
                }
                else if (key.EndsWith("Preprocessor"))
                {
                    ControlNetPreprocessors[key] = data;
                }
                if (NodeToFeatureMap.TryGetValue(key, out string featureId))
                {
                    FeaturesSupported.Add(featureId);
                }
            }
        }
    }

    public static LockObject ValueAssignmentLocker = new();

    public static T2IRegisteredParam<string> WorkflowParam, CustomWorkflowParam, SamplerParam, SchedulerParam, RefinerUpscaleMethod, ControlNetPreprocessorParam, UseIPAdapterForRevision;

    public static T2IRegisteredParam<bool> AITemplateParam, DebugRegionalPrompting;

    public static T2IRegisteredParam<double> IPAdapterWeight;

    public static List<string> UpscalerModels = new() { "latent-nearest-exact", "latent-bilinear", "latent-area", "latent-bicubic", "latent-bislerp", "pixel-nearest-exact", "pixel-bilinear", "pixel-area", "pixel-bicubic" },
        Samplers = new() { "euler", "euler_ancestral", "heun", "dpm_2", "dpm_2_ancestral", "lms", "dpm_fast", "dpm_adaptive", "dpmpp_2s_ancestral", "dpmpp_sde", "dpmpp_2m", "dpmpp_2m_sde", "ddim", "uni_pc", "uni_pc_bh2" },
        Schedulers = new() { "normal", "karras", "exponential", "simple", "ddim_uniform" };

    public static List<string> IPAdapterModels = new() { "None" };

    public static ConcurrentDictionary<string, JToken> ControlNetPreprocessors = new() { ["None"] = null };

    /// <summary>All current custom workflow IDs. Values are just a copy of the name (because C# lacks a ConcurrentList).</summary>
    public static ConcurrentDictionary<string, string> CustomWorkflows = new();

    public static T2IParamGroup ComfyGroup, ComfyAdvancedGroup;

    public override void OnInit()
    {
        UseIPAdapterForRevision = T2IParamTypes.Register<string>(new("Use IP-Adapter", "Use IP-Adapter for ReVision input handling.",
            "None", IgnoreIf: "None", FeatureFlag: "ipadapter", GetValues: _ => IPAdapterModels, Group: T2IParamTypes.GroupRevision, OrderPriority: 15, ChangeWeight: 1
            ));
        IPAdapterWeight = T2IParamTypes.Register<double>(new("IP-Adapter Weight", "Weight to use with IP-Adapter (if enabled).",
            "1", Min: -1, Max: 3, Step: 0.05, IgnoreIf: "1", FeatureFlag: "ipadapter", Group: T2IParamTypes.GroupRevision, ViewType: ParamViewType.SLIDER, OrderPriority: 16
            ));
        ComfyGroup = new("ComfyUI", Toggles: false, Open: false);
        ComfyAdvancedGroup = new("ComfyUI Advanced", Toggles: false, IsAdvanced: true, Open: false);
        WorkflowParam = T2IParamTypes.Register<string>(new("[ComfyUI] Workflow", "What hand-written specialty workflow to use in ComfyUI (files in 'Workflows' folder within the ComfyUI extension)",
            "basic", Toggleable: true, FeatureFlag: "comfyui", Group: ComfyAdvancedGroup, IsAdvanced: true, VisibleNormally: false, ChangeWeight: 8,
            GetValues: (_) => Workflows.Keys.ToList()
            ));
        CustomWorkflowParam = T2IParamTypes.Register<string>(new("[ComfyUI] Custom Workflow", "What custom workflow to use in ComfyUI (built in the Comfy Workflow Editor tab)",
            "", Toggleable: true, FeatureFlag: "comfyui", Group: ComfyGroup, IsAdvanced: true, ValidateValues: false, ChangeWeight: 8,
            GetValues: (_) => CustomWorkflows.Keys.Order().ToList(),
            Clean: (_, val) => CustomWorkflows.ContainsKey(val) ? $"PARSED%{val}%{ReadCustomWorkflow(val)["prompt"]}" : val,
            MetadataFormat: v => v.StartsWith("PARSED%") ? v.After("%").Before("%") : v
            ));
        SamplerParam = T2IParamTypes.Register<string>(new("Sampler", "Sampler type (for ComfyUI)",
            "euler", Toggleable: true, FeatureFlag: "comfyui", Group: ComfyGroup,
            GetValues: (_) => Samplers
            ));
        SchedulerParam = T2IParamTypes.Register<string>(new("Scheduler", "Scheduler type (for ComfyUI)",
            "normal", Toggleable: true, FeatureFlag: "comfyui", Group: ComfyGroup,
            GetValues: (_) => Schedulers
            ));
        AITemplateParam = T2IParamTypes.Register<bool>(new("Enable AITemplate", "If checked, enables AITemplate for ComfyUI generations (UNet only). Only compatible with some GPUs.",
            "false", IgnoreIf: "false", FeatureFlag: "aitemplate", Group: ComfyGroup, ChangeWeight: 5
            ));
        RefinerUpscaleMethod = T2IParamTypes.Register<string>(new("Refiner Upscale Method", "How to upscale the image, if upscaling is used.",
            "pixel-bilinear", Group: T2IParamTypes.GroupRefiners, OrderPriority: 1, FeatureFlag: "comfyui", ChangeWeight: 1,
            GetValues: (_) => UpscalerModels
            ));
        ControlNetPreprocessorParam = T2IParamTypes.Register<string>(new("ControlNet Preprocessor", "The preprocessor to use on the ControlNet input image.\nIf toggled off, will be automatically selected.\nUse 'None' to disable preprocessing.",
            "None", Toggleable: true, FeatureFlag: "controlnet", Group: T2IParamTypes.GroupControlNet, OrderPriority: 3, GetValues: (_) => ControlNetPreprocessors.Keys.Order().OrderBy(v => v == "None" ? -1 : 0).ToList(), ChangeWeight: 2
            ));
        DebugRegionalPrompting = T2IParamTypes.Register<bool>(new("Debug Regional Prompting", "If checked, outputs masks from regional prompting for debug reasons.",
            "false", IgnoreIf: "false", FeatureFlag: "comfyui", VisibleNormally: false
            ));
        Program.Backends.RegisterBackendType<ComfyUIAPIBackend>("comfyui_api", "ComfyUI API By URL", "A backend powered by a pre-existing installation of ComfyUI, referenced via API base URL.", true);
        Program.Backends.RegisterBackendType<ComfyUISelfStartBackend>("comfyui_selfstart", "ComfyUI Self-Starting", "A backend powered by a pre-existing installation of the ComfyUI, automatically launched and managed by this UI server.");
        API.RegisterAPICall(ComfySaveWorkflow);
        API.RegisterAPICall(ComfyReadWorkflow);
        API.RegisterAPICall(ComfyListWorkflows);
        API.RegisterAPICall(ComfyDeleteWorkflow);
        API.RegisterAPICall(ComfyGetGeneratedWorkflow);
    }

    /// <summary>API route to save a comfy workflow object to persistent file.</summary>
    public async Task<JObject> ComfySaveWorkflow(string name, string workflow, string prompt, string custom_params)
    {
        string path = Utilities.StrictFilenameClean(name);
        CustomWorkflows.TryAdd(path, path);
        Directory.CreateDirectory($"{Folder}/CustomWorkflows");
        path = $"{Folder}/CustomWorkflows/{path}.json";
        JObject data = new()
        {
            ["workflow"] = workflow,
            ["prompt"] = prompt,
            ["custom_params"] = custom_params
        };
        File.WriteAllBytes(path, data.ToString().EncodeUTF8());
        return new JObject() { ["success"] = true };
    }

    /// <summary>Method to directly read a custom workflow file.</summary>
    public static JObject ReadCustomWorkflow(string name)
    {
        string path = Utilities.StrictFilenameClean(name);
        path = $"{Folder}/CustomWorkflows/{path}.json";
        if (!File.Exists(path))
        {
            return new JObject() { ["error"] = "Unknown custom workflow name." };
        }
        string data = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        return data.ParseToJson();
    }

    /// <summary>API route to read a comfy workflow object from persistent file.</summary>
    public async Task<JObject> ComfyReadWorkflow(string name)
    {
        JObject val = ReadCustomWorkflow(name);
        if (val.ContainsKey("error"))
        {
            return val;
        }
        return new JObject() { ["result"] = val };
    }

    /// <summary>API route to read a list of available Comfy custom workflows.</summary>
    public async Task<JObject> ComfyListWorkflows()
    {
        return new JObject() { ["workflows"] = JToken.FromObject(CustomWorkflows.Keys.Order().ToList()) };
    }

    /// <summary>API route to read a delete a saved Comfy custom workflows.</summary>
    public async Task<JObject> ComfyDeleteWorkflow(string name)
    {
        string path = Utilities.StrictFilenameClean(name);
        CustomWorkflows.Remove(path, out _);
        path = $"{Folder}/CustomWorkflows/{path}.json";
        if (!File.Exists(path))
        {
            return new JObject() { ["error"] = "Unknown custom workflow name." };
        }
        File.Delete(path);
        return new JObject() { ["success"] = true };
    }

    /// <summary>API route to get a generated workflow for a T2I input.</summary>
    public async Task<JObject> ComfyGetGeneratedWorkflow(Session session, JObject rawInput)
    {
        T2IParamInput input;
        try
        {
            input = T2IAPI.RequestToParams(session, rawInput);
        }
        catch (InvalidOperationException ex)
        {
            return new JObject() { ["error"] = ex.Message };
        }
        catch (InvalidDataException ex)
        {
            return new JObject() { ["error"] = ex.Message };
        }
        string format = ComfyBackendsDirect().FirstOrDefault().Item3.SupportedFeatures.Contains("folderbackslash") ? "\\" : "/";
        string flow = ComfyUIAPIAbstractBackend.CreateWorkflow(input, w => w, format);
        return new JObject() { ["workflow"] = flow };
    }

    public override void OnPreLaunch()
    {
        WebServer.WebApp.Map("/ComfyBackendDirect/{*Path}", ComfyBackendDirectHandler);
    }

    public static IEnumerable<(HttpClient, string, AbstractT2IBackend)> ComfyBackendsDirect()
    {
        foreach (ComfyUIAPIAbstractBackend backend in RunningComfyBackends)
        {
            yield return (backend.HttpClient, backend.Address, backend);
        }
        foreach (SwarmSwarmBackend swarmBackend in Program.Backends.RunningBackendsOfType<SwarmSwarmBackend>().Where(b => b.RemoteBackendTypes.Any(b => b.StartsWith("comfyui_"))))
        {
            yield return (swarmBackend.HttpClient, $"{swarmBackend.Settings.Address}/ComfyBackendDirect", swarmBackend);
        }
    }

    public class ComfyUser
    {
        public ConcurrentDictionary<ComfyClientData, ComfyClientData> Clients = new();

        public string MasterSID;

        public int TotalQueue => Clients.Values.Sum(c => c.QueueRemaining);

        public SemaphoreSlim Lock = new(1, 1);

        public volatile JObject LastExecuting, LastProgress;
    }

    public class ComfyClientData
    {
        public ClientWebSocket Socket;

        public string SID;

        public volatile int QueueRemaining;

        public string LastNode;

        public volatile JObject LastExecuting, LastProgress;

        public string Address;

        public AbstractT2IBackend Backend;

        public static HashSet<string> ModelNameInputNames = new() { "ckpt_name", "vae_name", "lora_name", "clip_name", "control_net_name", "style_model_name", "model_path", "lora_names" };

        public void FixUpPrompt(JObject prompt)
        {
            bool isBackSlash = Backend.SupportedFeatures.Contains("folderbackslash");
            foreach (JProperty node in prompt.Properties())
            {
                JObject inputs = node.Value["inputs"] as JObject;
                if (inputs is not null)
                {
                    foreach (JProperty input in inputs.Properties())
                    {
                        if (ModelNameInputNames.Contains(input.Name) && input.Value.Type == JTokenType.String)
                        {
                            string val = input.Value.ToString();
                            if (isBackSlash)
                            {
                                val = val.Replace("/", "\\");
                            }
                            else
                            {
                                val = val.Replace("\\", "/");
                            }
                            input.Value = val;
                        }
                    }
                }
            }
        }
    }

    public ConcurrentDictionary<string, ComfyUser> Users = new();

    /// <summary>Web route for viewing output images. This just works as a simple proxy.</summary>
    public async Task ComfyBackendDirectHandler(HttpContext context)
    {
        if (context.Response.StatusCode == 404)
        {
            return;
        }
        List<(HttpClient, string, AbstractT2IBackend)> allBackends = ComfyBackendsDirect().ToList();
        (HttpClient webClient, string address, AbstractT2IBackend backend) = allBackends.FirstOrDefault();
        if (webClient is null)
        {
            context.Response.ContentType = "text/html";
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("<!DOCTYPE html><html><head><stylesheet>body{background-color:#101010;color:#eeeeee;}</stylesheet></head><body><span class=\"comfy-failed-to-load\">No ComfyUI backend available, loading failed.</span></body></html>");
            await context.Response.CompleteAsync();
            return;
        }
        if (!context.Request.Cookies.TryGetValue("comfy_domulti", out string doMultiStr) || doMultiStr != "true")
        {
            allBackends = new() { (webClient, address, backend) };
        }
        string path = context.Request.Path.Value;
        path = path.After("/ComfyBackendDirect");
        if (path.StartsWith("/"))
        {
            path = path[1..];
        }
        if (!string.IsNullOrWhiteSpace(context.Request.QueryString.Value))
        {
            path = $"{path}{context.Request.QueryString.Value}";
        }
        if (context.WebSockets.IsWebSocketRequest)
        {
            WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
            List<Task> tasks = new();
            ComfyUser user = new();
            foreach ((_, string addressLocal, AbstractT2IBackend backendLocal) in allBackends)
            {
                string scheme = addressLocal.BeforeAndAfter("://", out string addr);
                scheme = scheme == "http" ? "ws" : "wss";
                ClientWebSocket outSocket = new();
                outSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                await outSocket.ConnectAsync(new Uri($"{scheme}://{addr}/{path}"), Program.GlobalProgramCancel);
                ComfyClientData client = new() { Address = addressLocal, Backend = backendLocal };
                user.Clients.TryAdd(client, client);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        byte[] recvBuf = new byte[10 * 1024 * 1024];
                        while (true)
                        {
                            WebSocketReceiveResult received = await outSocket.ReceiveAsync(recvBuf, Program.GlobalProgramCancel);
                            if (received.MessageType != WebSocketMessageType.Close)
                            {
                                Memory<byte> toSend = recvBuf.AsMemory(0, received.Count);
                                await user.Lock.WaitAsync();
                                try
                                {
                                    bool isJson = received.MessageType == WebSocketMessageType.Text && received.EndOfMessage && received.Count < 8192 * 10 && recvBuf[0] == '{';
                                    if (isJson)
                                    {
                                        try
                                        {
                                            JObject parsed = StringConversionHelper.UTF8Encoding.GetString(recvBuf[0..received.Count]).ParseToJson();
                                            JToken typeTok = parsed["type"];
                                            if (typeTok is not null)
                                            {
                                                string type = typeTok.ToString();
                                                if (type == "executing")
                                                {
                                                    client.LastExecuting = parsed;
                                                    user.LastExecuting = parsed;
                                                }
                                                else if (type == "progress")
                                                {
                                                    client.LastProgress = parsed;
                                                    user.LastProgress = parsed;
                                                }
                                            }
                                            JToken dataTok = parsed["data"];
                                            if (dataTok is JObject dataObj)
                                            {
                                                if (dataObj.TryGetValue("sid", out JToken sidTok))
                                                {
                                                    if (client.SID is not null)
                                                    {
                                                        Users.TryRemove(client.SID, out _);
                                                    }
                                                    client.SID = sidTok.ToString();
                                                    Users.TryAdd(client.SID, user);
                                                    if (user.MasterSID is null)
                                                    {
                                                        user.MasterSID = client.SID;
                                                    }
                                                    else
                                                    {
                                                        parsed["data"]["sid"] = user.MasterSID;
                                                        toSend = Encoding.UTF8.GetBytes(parsed.ToString());
                                                    }
                                                }
                                                if (dataObj.TryGetValue("node", out JToken nodeTok))
                                                {
                                                    client.LastNode = nodeTok.ToString();
                                                }
                                                JToken queueRemTok = dataObj["status"]?["exec_info"]?["queue_remaining"];
                                                if (queueRemTok is not null)
                                                {
                                                    client.QueueRemaining = queueRemTok.Value<int>();
                                                    dataObj["status"]["exec_info"]["queue_remaining"] = user.TotalQueue;
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Logs.Error($"Failed to parse ComfyUI message: {ex}");
                                        }
                                    }
                                    if (!isJson)
                                    {
                                        if (client.LastExecuting is not null && (client.LastExecuting != user.LastExecuting || client.LastProgress != user.LastProgress))
                                        {
                                            user.LastExecuting = client.LastExecuting;
                                            await socket.SendAsync(StringConversionHelper.UTF8Encoding.GetBytes(client.LastExecuting.ToString()), WebSocketMessageType.Text, true, Program.GlobalProgramCancel);
                                        }
                                        if (client.LastProgress is not null && (client.LastExecuting != user.LastExecuting || client.LastProgress != user.LastProgress))
                                        {
                                            user.LastProgress = client.LastProgress;
                                            await socket.SendAsync(StringConversionHelper.UTF8Encoding.GetBytes(client.LastProgress.ToString()), WebSocketMessageType.Text, true, Program.GlobalProgramCancel);
                                        }
                                    }
                                    await socket.SendAsync(toSend, received.MessageType, received.EndOfMessage, Program.GlobalProgramCancel);
                                }
                                finally
                                {
                                    user.Lock.Release();
                                }
                            }
                            if (socket.CloseStatus.HasValue)
                            {
                                await socket.CloseAsync(socket.CloseStatus.Value, socket.CloseStatusDescription, Program.GlobalProgramCancel);
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logs.Debug($"ComfyUI redirection failed: {ex}");
                    }
                    finally
                    {
                        Users.TryRemove(client.SID, out _);
                        user.Clients.TryRemove(client, out _);
                    }
                }));
            }
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    byte[] recvBuf = new byte[10 * 1024 * 1024];
                    while (true)
                    {
                        // TODO: Should this input be allowed to remain open forever? Need a timeout, but the ComfyUI websocket doesn't seem to keepalive properly.
                        WebSocketReceiveResult received = await socket.ReceiveAsync(recvBuf, Program.GlobalProgramCancel);
                        foreach (ComfyClientData client in user.Clients.Values)
                        {
                            if (received.MessageType != WebSocketMessageType.Close)
                            {
                                await client.Socket.SendAsync(recvBuf.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, Program.GlobalProgramCancel);
                            }
                            if (socket.CloseStatus.HasValue)
                            {
                                await client.Socket.CloseAsync(socket.CloseStatus.Value, socket.CloseStatusDescription, Program.GlobalProgramCancel);
                                return;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logs.Debug($"ComfyUI redirection failed: {ex}");
                }
                finally
                {
                    Users.TryRemove(user.MasterSID, out _);
                }
            }));
            await Task.WhenAll(tasks);
            return;
        }
        // This code is utterly silly, but it's incredibly fragile, don't touch without significant testing
        HttpResponseMessage response;
        if (context.Request.Method == "POST")
        {
            HttpContent content = null;
            if (path == "prompt")
            {
                try
                {
                    using MemoryStream memStream = new();
                    await context.Request.Body.CopyToAsync(memStream);
                    byte[] data = memStream.ToArray();
                    JObject parsed = StringConversionHelper.UTF8Encoding.GetString(data).ParseToJson();
                    if (parsed.TryGetValue("client_id", out JToken clientIdTok))
                    {
                        string sid = clientIdTok.ToString();
                        if (Users.TryGetValue(sid, out ComfyUser user))
                        {
                            await user.Lock.WaitAsync();
                            try
                            {
                                JObject prompt = parsed["prompt"] as JObject;
                                int preferredBackendIndex = prompt["swarm_prefer"]?.Value<int>() ?? -1;
                                prompt.Remove("swarm_prefer");
                                List<ComfyClientData> available = user.Clients.Values.ToList();
                                ComfyClientData client = user.Clients.Values.MinBy(c => c.QueueRemaining);
                                if (preferredBackendIndex >= 0)
                                {
                                    client = available[preferredBackendIndex % available.Count];
                                }
                                if (client?.SID is not null)
                                {
                                    client.QueueRemaining++;
                                    address = client.Address;
                                    parsed["client_id"] = client.SID;
                                    client.FixUpPrompt(parsed["prompt"] as JObject);
                                }
                            }
                            finally
                            {
                                user.Lock.Release();
                            }
                        }
                    }
                    content = Utilities.JSONContent(parsed);
                }
                catch (Exception ex)
                {
                    Logs.Debug($"ComfyUI redirection failed - prompt json parse: {ex}");
                }
            }
            HttpRequestMessage request = new(new HttpMethod("POST"), $"{address}/{path}") { Content = content ?? new StreamContent(context.Request.Body) };
            if (content is null)
            {
                request.Content.Headers.Add("Content-Type", context.Request.ContentType);
            }
            response = await webClient.SendAsync(request);
        }
        else
        {
            if (path.StartsWith("view?filename="))
            {
                List<Task<HttpResponseMessage>> requests = new();
                foreach ((HttpClient clientLocal, string addressLocal, AbstractT2IBackend backendLocal) in allBackends)
                {
                    requests.Add(clientLocal.SendAsync(new(new(context.Request.Method), $"{addressLocal}/{path}")));
                }
                await Task.WhenAll(requests);
                response = requests.Select(r => r.Result).FirstOrDefault(r => r.StatusCode == HttpStatusCode.OK) ?? requests.First().Result;
            }
            else
            {
                response = await webClient.SendAsync(new(new(context.Request.Method), $"{address}/{path}"));
            }
        }
        int code = (int)response.StatusCode;
        if (code != 200)
        {
            Logs.Debug($"ComfyUI redirection gave non-200 code: '{code}' for URL: {context.Request.Method} '{path}'");
        }
        Logs.Verbose($"Comfy Redir status code {code} from {context.Response.StatusCode} and type {response.Content.Headers.ContentType} for {context.Request.Method} '{path}'");
        context.Response.StatusCode = code;
        context.Response.ContentType = response.Content.Headers.ContentType.ToString();
        await response.Content.CopyToAsync(context.Response.Body);
        await context.Response.CompleteAsync();
    }
}
