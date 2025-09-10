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
	private readonly HttpClient _httpClient;

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
		_httpClient = new HttpClient();
		// Register drift event handlers
		cspClientMessageTypeManager.RegisterOnlineEvent<PlayerConnectPacket>(OnPlayerConnect);
		cspClientMessageTypeManager.RegisterOnlineEvent<DriftCompletePacket>(OnDriftComplete);
		cspClientMessageTypeManager.RegisterOnlineEvent<SessionEndPacket>(OnSessionEnd);
		cspClientMessageTypeManager.RegisterOnlineEvent<PersonalBestPacket>(OnPersonalBestRequest);
		cspClientMessageTypeManager.RegisterOnlineEvent<AchievementPacket>(OnAchievement);
        
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
                Log.Error(ex, "DriftGod: Failed to load Lua script");
                throw;
            }
        }
        else
        {
            Log.Warning("DriftGod: Client messages are disabled. The drift UI will not work!");
        }
        
        Log.Information("DriftGodPlugin by Living God - Initialized");
    }


    private void OnClientConnected(ACTcpClient client, EventArgs args)
	{
		Log.Information("DriftGod: {PlayerName} connected", client.Name);
	}
	
	private async Task TestFirebaseConnection()
    {
        try
        {
            var response = await _httpClient.GetAsync("https://firestore.googleapis.com/v1/projects/driftgod-leaderboard/databases/(default)/documents/drift_leaderboard/76561198257607913");
            var content = await response.Content.ReadAsStringAsync();
            Log.Information("DriftGod: Firebase test response: {Content}", content);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DriftGod: Firebase connection test failed");
        }
    }
	
	public async Task<int> GetPlayerRank(ulong steamId)
	{
		try
		{
			var response = await _httpClient.GetAsync("https://firestore.googleapis.com/v1/projects/driftgod-leaderboard/databases/(default)/documents/drift_leaderboard?orderBy=best_score%20desc");
			var content = await response.Content.ReadAsStringAsync();
			
			// Parse the JSON to find player rank
			using var document = System.Text.Json.JsonDocument.Parse(content);
			var documents = document.RootElement.GetProperty("documents");
			
			int rank = 1;
			foreach (var doc in documents.EnumerateArray())
			{
				var docName = doc.GetProperty("name").GetString();
				if (docName != null && docName.Contains(steamId.ToString()))
				{
					Log.Information("DriftGod: Found player at rank {Rank}", rank);
					return rank;
				}
				rank++;
			}
			
			Log.Warning("DriftGod: Player not found in leaderboard");
			return -1;
		}
		catch (Exception ex)
		{
			Log.Error(ex, "DriftGod: Failed to get player rank");
			return -1;
		}
	}
	
	public async Task UpdateFirebasePlayer(ulong steamId, long score, string playerName, PlayerDriftStats stats)
	{
		try
		{
			var url = $"https://firestore.googleapis.com/v1/projects/driftgod-leaderboard/databases/(default)/documents/drift_leaderboard/{steamId}";
			
			var body = new
			{
				fields = new
				{
					best_score = new { integerValue = score.ToString() },
					player_name = new { stringValue = playerName },
					best_score_car = new { stringValue = stats.BestScoreCarName },
					best_score_angle = new { doubleValue = stats.BestScoreMaxAngle },
					longest_drift = new { doubleValue = stats.LongestSingleDrift },
					total_drifts = new { integerValue = stats.TotalDrifts.ToString() },
					last_updated = new { timestampValue = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") }
				}
			};
			
			var json = System.Text.Json.JsonSerializer.Serialize(body);
			var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
			
			var response = await _httpClient.PatchAsync(url, content);
			
			if (response.IsSuccessStatusCode)
			{
				Log.Information("DriftGod: Updated Firebase with complete data for {PlayerName}", playerName);
			}
			else
			{
				Log.Warning("DriftGod: Failed to update Firebase for {PlayerName} - Status: {Status}", playerName, response.StatusCode);
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, "DriftGod: Firebase update failed for {PlayerName}", playerName);
		}
	}
	
	private async Task SyncPlayerToFirebase(ACTcpClient client, DriftSession session)
	{
		// Always update Firebase with current JSON data
		await UpdateFirebasePlayer(client.Guid, session.PersonalBest, client.Name, session.GetStats());
		
		// Get rank from Firebase
		var rank = await GetPlayerRank(client.Guid);
		
		// Send rank to client
		client.SendPacket(new PlayerRankPacket
		{
			CurrentRank = rank
		});
		
		Log.Information("DriftGod: Synced {PlayerName} - PB: {PB}, Rank: {Rank}", client.Name, session.PersonalBest, rank);
	}
	
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		Log.Information("DriftGodPlugin by Living God - Started successfully!");
		
		while (!stoppingToken.IsCancellationRequested)
		{
			await Task.Delay(5000, stoppingToken);
		}
	}

    private void OnClientDisconnected(ACTcpClient client, EventArgs args)
    {
        Log.Information("DriftGod: {PlayerName} disconnected", client.Name);
        
        if (_driftSessions.TryGetValue(client, out var session))
        {
            session.SaveData();
            _driftSessions.Remove(client);
        }
    }
	
	private void OnPersonalBestRequest(ACTcpClient sender, PersonalBestPacket packet)
	{
		// Not used for incoming requests, only for outgoing PB data
	}
	
	private async void OnPlayerConnect(ACTcpClient sender, PlayerConnectPacket packet)
	{
		Log.Information("DriftGod: Player {PlayerName} sent connect event", sender.Name);
		
		// Create drift session (loads PB from JSON)
		var driftSession = new DriftSession(sender, this);
		_driftSessions[sender] = driftSession;
		
		// Sync JSON PB to Firebase and get rank
		await SyncPlayerToFirebase(sender, driftSession);
		
		// Send personal best back to client (from JSON)
		sender.SendPacket(new PersonalBestPacket
		{
			PersonalBest = driftSession.PersonalBest
		});
		
		Log.Information("DriftGod: Sent PB {PersonalBest} to {PlayerName}", driftSession.PersonalBest, sender.Name);
	}
	
	private void OnAchievement(ACTcpClient sender, AchievementPacket packet)
	{
		if (_driftSessions.TryGetValue(sender, out var session))
		{
			var stats = session.GetStats();
			string achievementName = "";
			
			switch (packet.AchievementType)
			{
				case 1:
					stats.TimesReachedGeometryStudent++;
					achievementName = "Geometry Student";
					break;
				case 2:
					stats.TimesReachedDriftSpecialist++;
					achievementName = "Drift Specialist";
					break;
				case 3:
					stats.TimesReachedLateralMaster++;
					achievementName = "Lateral Master";
					break;
				case 4:
					stats.TimesReachedProfessorSlideways++;
					achievementName = "Professor Slideways";
					break;
				case 5:
					stats.TimesReachedDriftGod++;
					achievementName = "Drift God";
					break;
			}
			
			Log.Information("DriftGod: {PlayerName} achieved {Achievement}", sender.Name, achievementName);
		}
	}

	private void OnDriftComplete(ACTcpClient sender, DriftCompletePacket packet)
	{
		long actualScore = packet.Score;
		float actualAngle = packet.AverageAngle;
		float actualDuration = packet.Duration;  // Get the duration
		string carModel = sender.EntryCar?.Model ?? "Unknown";
		
		Log.Information("DriftGod: Drift completed by {PlayerName} - Score: {Score}, Angle: {Angle:F1}°, Duration: {Duration:F1}s", 
			sender.Name, actualScore, actualAngle, actualDuration);
			
		if (_driftSessions.TryGetValue(sender, out var session))
		{
			bool wasNewPB = session.OnDriftScoreReceived(actualScore, actualAngle, 0, actualDuration, carModel);
			
			// Broadcast significant scores to other players
			if (actualScore > 5000)
			{
				// Find the correct index in EntryCarManager
				int correctIndex = -1;
				for (int i = 0; i < _entryCarManager.EntryCars.Length; i++)
				{
					if (_entryCarManager.EntryCars[i].Client == sender)
					{
						correctIndex = i;
						break;
					}
				}

				var broadcast = new DriftBroadcastPacket
				{
					Score = (int)actualScore,
					IsPersonalBest = (byte)(wasNewPB ? 1 : 0),
					PlayerId = (byte)correctIndex  // Use the EntryCarManager index
				};

				Log.Information("DriftGod: Sending broadcast with PB flag: {PBFlag}", wasNewPB ? 1 : 0);
				
				// Send to all other connected clients (including self for testing)
				foreach (var entryCar in _entryCarManager.EntryCars)
				{
					if (entryCar.Client != null && entryCar.Client != sender && entryCar.Client.IsConnected)
					{
						entryCar.Client.SendPacket(broadcast);
					}
				}
				
				Log.Information("DriftGod: Broadcasting {PlayerName}'s {Score} point drift{PB}", 
					sender.Name, actualScore, wasNewPB ? " (NEW PB!)" : "");
			}
		}
	}

	private void OnSessionEnd(ACTcpClient sender, SessionEndPacket packet)
	{
		Log.Information("DriftGod: Session ended for {PlayerName}", sender.Name);
		
		// Handle session end data when we add those fields
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

    private readonly DriftGodPlugin _mainPlugin;

public DriftSession(ACTcpClient client, DriftGodPlugin mainPlugin)
    {
        Client = client;
        PlayerName = client.Name ?? "Unknown";
        SteamId = client.Guid;
        SessionStart = DateTime.UtcNow;
		_mainPlugin = mainPlugin;
		
		string currentCar = client.EntryCar?.Model ?? "Unknown";
        
        // Create data directory
        var dataDir = Path.Combine("drift-data", "players", SteamId.ToString());
        Directory.CreateDirectory(dataDir);
        _playerDataPath = Path.Combine(dataDir, "stats.json");
        
        LoadPlayerStats();
        
        Log.Information("DriftPlugin: Created session for {PlayerName} in {CarModel} - PB: {PersonalBest:N0} points", 
                       PlayerName, currentCar, PersonalBest);
    }
    
    public bool OnDriftScoreReceived(long score, float maxAngle, float maxSpeed, float duration, string carName)
    {
        SessionDrifts++;
        
        // Update session best
        if (score > SessionBestScore)
        {
            SessionBestScore = score;
        }
		
		if (duration > _stats.LongestSingleDrift)
		{
			_stats.LongestSingleDrift = duration;
			Log.Information("DriftGod: New longest drift for {PlayerName}: {Duration:F1}s", PlayerName, duration);
		}
        
        // Check for new personal best
        bool isNewPB = false;
        if (score > PersonalBest)
        {
            PersonalBest = score;
            isNewPB = true;
            
            // Update persistent stats for new personal best
            _stats.BestScore = score;
            _stats.BestScoreMaxAngle = maxAngle;
            _stats.BestScoreCarName = Client.EntryCar?.Model ?? "Unknown";
			
			// Update Firebase and recalculate rank
			_ = Task.Run(async () =>
			{
				await _mainPlugin.UpdateFirebasePlayer(SteamId, score, PlayerName, _stats);
				var newRank = await _mainPlugin.GetPlayerRank(SteamId);
				
				if (newRank > 0)
				{
					Client.SendPacket(new PlayerRankPacket
					{
						CurrentRank = newRank
					});
					Log.Information("DriftGod: {PlayerName} new PB! New rank: {Rank}", PlayerName, newRank);
				}
			});
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
				 return isNewPB;
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
    public float BestScoreMaxAngle { get; set; }
    public string BestScoreCarName { get; set; } = string.Empty;
    
    // Drift skill metrics
    public float AverageAngle { get; set; }  // Average of top 10 angles
    public float LongestSingleDrift { get; set; }  // Duration in seconds
    public float LongestTimeAtMaxCombo { get; set; }  // Time held at 5x combo
    
    // Behavioral tracking
    public int MostUsedGear { get; set; }  // Favorite gear
    public string FavoriteCar { get; set; } = string.Empty;  // Most driven car
    public int ChatMessagesSent { get; set; }
    
    // Session and general stats
    public int TotalDrifts { get; set; }
    public long TotalPoints { get; set; }
    public long AverageScore { get; set; }
    public TimeSpan TotalSessionTime { get; set; }
    
    // Consistency metrics
    public int ConsecutiveDriftsWithoutCrash { get; set; }
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
	
	[OnlineEventField(Name = "duration")]
    public float Duration;
}

[OnlineEvent(Key = "DriftGod_sessionEnd")]
public class SessionEndPacket : OnlineEvent<SessionEndPacket>
{

}

[OnlineEvent(Key = "DriftGod_personalBest")]
public class PersonalBestPacket : OnlineEvent<PersonalBestPacket>
{
    [OnlineEventField(Name = "personalBest")]
    public long PersonalBest;
}

[OnlineEvent(Key = "DriftGod_achievement")]
public class AchievementPacket : OnlineEvent<AchievementPacket>
{
    [OnlineEventField(Name = "achievementType")]
    public int AchievementType;
}

[OnlineEvent(Key = "DriftGod_driftBroadcast")]
public class DriftBroadcastPacket : OnlineEvent<DriftBroadcastPacket>
{
    [OnlineEventField(Name = "score")]
    public int Score;
    
    [OnlineEventField(Name = "isPersonalBest")]
    public byte IsPersonalBest;
    
    [OnlineEventField(Name = "playerId")]
    public byte PlayerId;  // We can use the car slot ID instead of name
}

[OnlineEvent(Key = "DriftGod_playerRank")]
public class PlayerRankPacket : OnlineEvent<PlayerRankPacket>
{
    [OnlineEventField(Name = "currentRank")]
    public int CurrentRank;
}