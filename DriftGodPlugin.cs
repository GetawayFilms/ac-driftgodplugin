using System.Reflection;
using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Server.Plugin;
using AssettoServer.Shared.Services;
using AssettoServer.Network.Tcp;
using AssettoServer.Network.ClientMessages;
using AssettoServer.Shared.Network.Packets.Outgoing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JetBrains.Annotations;
using Serilog;

namespace DriftGodPlugin;

public class DriftGodPlugin : CriticalBackgroundService, IAssettoServerAutostart
{
    private readonly DriftGodConfiguration _configuration;
    private readonly EntryCarManager _entryCarManager;
    private readonly CSPServerScriptProvider _scriptProvider;
    private readonly ACServerConfiguration _serverConfiguration;
    private readonly Dictionary<ACTcpClient, DriftSession> _driftSessions = new();
	private readonly CSPClientMessageTypeManager _cspClientMessageTypeManager;

    public DriftGodPlugin(
        DriftGodConfiguration configuration,
        EntryCarManager entryCarManager, 
        CSPServerScriptProvider scriptProvider,
        ACServerConfiguration serverConfiguration,
		CSPClientMessageTypeManager cspClientMessageTypeManager,
        ILoggerFactory loggerFactory,
        IHostApplicationLifetime applicationLifetime) : base(applicationLifetime)
		
    {
        _configuration = configuration;
        _entryCarManager = entryCarManager;
        _scriptProvider = scriptProvider;
        _serverConfiguration = serverConfiguration;
		_cspClientMessageTypeManager = cspClientMessageTypeManager;
		// Register drift event handlers
		cspClientMessageTypeManager.RegisterOnlineEvent<PlayerConnectPacket>(OnPlayerConnect);
		cspClientMessageTypeManager.RegisterOnlineEvent<DriftCompletePacket>(OnDriftComplete);
		cspClientMessageTypeManager.RegisterOnlineEvent<SessionEndPacket>(OnSessionEnd);
        
        // Hook into client connection events
        _entryCarManager.ClientConnected += OnClientConnected;
        _entryCarManager.ClientDisconnected += OnClientDisconnected;
        
        
        // Load and register the Lua script for all clients
        if (_serverConfiguration.Extra.EnableClientMessages)
        {
            try 
            {
                // Get the plugin directory
                string pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                string luaScriptPath = Path.Combine(pluginDirectory, "lua", "drift_god.lua");
                
                // Check if the Lua file exists
                if (!File.Exists(luaScriptPath))
                {
                    throw new FileNotFoundException($"Lua script not found at: {luaScriptPath}");
                }
                
                // Read the Lua script from file
                var luaScript = File.ReadAllText(luaScriptPath);
                _scriptProvider.AddScript(luaScript, "drift_god.lua");
                
                Log.Information("DriftGodPlugin by Living God: Lua script loaded from {LuaScriptPath} and distributed to clients", luaScriptPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "DriftGodPlugin: Failed to load Lua script");
                throw;
            }
        }
        else
        {
            Log.Warning("DriftGodPlugin: Client messages are disabled. The drift UI will not work!");
        }
        
        Log.Information("DriftGodPlugin by Living God - Initialized");
    }


    private void OnClientConnected(ACTcpClient client, EventArgs args)
	{
		Log.Information("DriftGodPlugin: {PlayerName} connected", client.Name);
	}

    private void OnClientDisconnected(ACTcpClient client, EventArgs args)
    {
        Log.Information("DriftGodPlugin: {PlayerName} disconnected", client.Name);
        
        if (_driftSessions.TryGetValue(client, out var session))
        {
            session.SaveData();
            _driftSessions.Remove(client);
        }
    }
	
	private void OnPlayerConnect(ACTcpClient sender, PlayerConnectPacket packet)
	{
		Log.Information("DriftGod: Player {PlayerName} sent connect event", sender.Name);
		
		// Create drift session when we receive the connect event
		var driftSession = new DriftSession(sender);
		_driftSessions[sender] = driftSession;
	}

	private void OnDriftComplete(ACTcpClient sender, DriftCompletePacket packet)
	{
		// Convert the scaled values back to actual values
		long actualScore = packet.Score;
		float actualAngle = packet.AverageAngle;
		
		Log.Information("DriftGod: Drift completed by {PlayerName} - Score: {Score}, Angle: {Angle:F1}°, Combo: {Combo:F1}x", 
			sender.Name, actualScore, actualAngle, packet.AverageCombo);
			
		if (_driftSessions.TryGetValue(sender, out var session))
		{
			session.OnDriftScoreReceived(actualScore, actualAngle, 0, 0, "");
		}
	}

	private void OnSessionEnd(ACTcpClient sender, SessionEndPacket packet)
	{
		Log.Information("DriftGod: Session ended for {PlayerName}", sender.Name);
		
		// Handle session end data when we add those fields
	}

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("DriftGodPlugin by Living God - Started successfully!");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(5000, stoppingToken);
        }
    }
}

public class DriftSession
{
    public ACTcpClient Client { get; }
    public string PlayerName { get; }
    public ulong SteamId { get; }
    
    // Session statistics
    public long PersonalBest { get; private set; }
    public int SessionDrifts { get; private set; }
    public long SessionBestScore { get; private set; }
    public DateTime SessionStart { get; }
    
    // Persistent player data
    private PlayerDriftStats _stats = new();
    private readonly string _playerDataPath;

    public DriftSession(ACTcpClient client)
    {
        Client = client;
        PlayerName = client.Name ?? "Unknown";
        SteamId = client.Guid;
        SessionStart = DateTime.UtcNow;
        
        // Create data directory
        var dataDir = Path.Combine("drift-data", "players", SteamId.ToString());
        Directory.CreateDirectory(dataDir);
        _playerDataPath = Path.Combine(dataDir, "stats.json");
        
        LoadPlayerStats();
        
        Log.Information("DriftPlugin: Created session for {PlayerName} - PB: {PersonalBest:N0} points", 
                       PlayerName, PersonalBest);
    }
    
    public void OnDriftScoreReceived(long score, float maxAngle, float maxSpeed, float duration, string carName)
    {
        SessionDrifts++;
        
        // Update session best
        if (score > SessionBestScore)
        {
            SessionBestScore = score;
        }
        
        // Check for new personal best
        bool isNewPB = false;
        if (score > PersonalBest)
        {
            PersonalBest = score;
            isNewPB = true;
            
            // Update persistent stats for new personal best
            _stats.BestScore = score;
            _stats.BestScoreDate = DateTime.UtcNow;
            _stats.BestScoreMaxAngle = maxAngle;
            _stats.BestScoreMaxSpeed = maxSpeed;
            _stats.BestScoreDuration = duration;
            _stats.BestScoreCarName = carName;
        }
        
        // Update general stats
        _stats.TotalDrifts++;
        _stats.TotalPoints += score;
        _stats.LastPlayedDate = DateTime.UtcNow;
        
        // Update averages
        _stats.AverageScore = _stats.TotalDrifts > 0 ? _stats.TotalPoints / _stats.TotalDrifts : 0;
        
        SavePlayerStats();
        
        Log.Debug("DriftGod: {PlayerName} scored {Score:N0} points{NewPB}", 
                 PlayerName, score, isNewPB ? " (NEW PB!)" : "");
    }
    
    private void LoadPlayerStats()
    {
        try
        {
            if (File.Exists(_playerDataPath))
            {
                var json = File.ReadAllText(_playerDataPath);
                _stats = System.Text.Json.JsonSerializer.Deserialize<PlayerDriftStats>(json) ?? new PlayerDriftStats();
                PersonalBest = _stats.BestScore;
            }
            else
            {
                _stats = new PlayerDriftStats();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DriftGod: Failed to load stats for {PlayerName}, creating new", PlayerName);
            _stats = new PlayerDriftStats();
        }
        
        // Update basic info each session
        _stats.PlayerName = PlayerName;
        _stats.SteamId = SteamId;
        _stats.FirstPlayedDate ??= DateTime.UtcNow;
    }
    
    private void SavePlayerStats()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_stats, new System.Text.Json.JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            File.WriteAllText(_playerDataPath, json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DriftGod: Failed to save stats for {PlayerName}", PlayerName);
        }
    }
    
    public void SaveData()
    {
        // Update final session stats before saving
        _stats.LastPlayedDate = DateTime.UtcNow;
        _stats.TotalSessionTime += DateTime.UtcNow - SessionStart;
        SavePlayerStats();
        
        Log.Information("DriftGod: Saved final session data for {PlayerName} - Session time: {Duration}, Session drifts: {Drifts}", 
                       PlayerName, DateTime.UtcNow - SessionStart, SessionDrifts);
    }
    
    public PlayerDriftStats GetStats()
    {
        return _stats;
    }
}

// Persistent player statistics
public class PlayerDriftStats
{
    public string PlayerName { get; set; } = string.Empty;
    public ulong SteamId { get; set; }
    
    // Best performance records
    public long BestScore { get; set; }
    public DateTime? BestScoreDate { get; set; }
    public float BestScoreMaxAngle { get; set; }
    public float BestScoreMaxSpeed { get; set; }
    public float BestScoreDuration { get; set; }
    public string BestScoreCarName { get; set; } = string.Empty;
    
    // Drift skill metrics
    public float PersonalBestAngle { get; set; }
    public float AverageTopAngle { get; set; }  // Average of top 10 angles
    public float LongestSingleDrift { get; set; }  // Duration in seconds
    public float LongestTimeAtMaxCombo { get; set; }  // Time held at 5x combo
    public float TopSpeedAchieved { get; set; }
    
    // Behavioral tracking
    public float TotalOffroadTime { get; set; }  // For offroad achievements
    public float TotalHandbrakeTime { get; set; }  // Total handbrake usage
    public int MostUsedGear { get; set; }  // Favorite gear
    public string FavoriteCar { get; set; } = string.Empty;  // Most driven car
    public int ChatMessagesSent { get; set; }
    
    // Session and general stats
    public int TotalDrifts { get; set; }
    public long TotalPoints { get; set; }
    public long AverageScore { get; set; }
    public TimeSpan TotalSessionTime { get; set; }
    public TimeSpan TotalPlayTime { get; set; }  // Lifetime across all sessions
    
    // Consistency metrics
    public int ConsecutiveDriftsWithoutCrash { get; set; }
    public int BestDriftStreak { get; set; }
    public float DriftConsistencyRating { get; set; }  // How close angles stay to average
    
    // Achievement milestone counters
    public int TimesReachedGeometryStudent { get; set; }  // 1000+ point drifts
    public int TimesReachedDriftSpecialist { get; set; }  // 4000+ point drifts
    public int TimesReachedLateralMaster { get; set; }  // 16000+ point drifts
    public int TimesReachedProfessorSlideways { get; set; }  // 64000+ point drifts
    public int TimesReachedDriftGod { get; set; }  // 256000+ point drifts
    
    // Dates
    public DateTime? FirstPlayedDate { get; set; }
    public DateTime? LastPlayedDate { get; set; }
}

[OnlineEvent(Key = "DriftGod_playerConnect")]
public class PlayerConnectPacket : OnlineEvent<PlayerConnectPacket>
{
    [OnlineEventField(Name = "connected")]
    public byte Connected;
}

[OnlineEvent(Key = "DriftGod_driftComplete")]
public class DriftCompletePacket : OnlineEvent<DriftCompletePacket>
{
    [OnlineEventField(Name = "score")]
    public int Score;
    
    [OnlineEventField(Name = "avgAngle")]
    public float AverageAngle;
    
    [OnlineEventField(Name = "avgCombo")]
    public float AverageCombo;
}

[OnlineEvent(Key = "DriftGod_sessionEnd")]
public class SessionEndPacket : OnlineEvent<SessionEndPacket>
{
    // Empty for now
}