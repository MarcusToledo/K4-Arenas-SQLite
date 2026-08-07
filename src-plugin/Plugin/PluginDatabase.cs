
using System.Threading;
using CounterStrikeSharp.API.Core;
using K4Arenas.Models;
using K4ArenaSharedApi;
using Microsoft.Data.Sqlite;
using Dapper;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using Microsoft.Extensions.Logging;

namespace K4Arenas;

public sealed partial class Plugin : BasePlugin
{
	// Serializes writes across every caller (LoadPlayerAsync, SavePlayerPreferencesAsync, PurgeDatabaseAsync, ...).
	// SQLite allows only one writer at a time; concurrent Task.Run() writes from multiple menus would otherwise
	// race for the file lock and surface as "database is locked" errors.
	private readonly SemaphoreSlim _databaseWriteLock = new(1, 1);

	private sealed class PlayerPreferencesRow
	{
		public int? Rifle { get; set; }
		public int? Sniper { get; set; }
		public int? Shotgun { get; set; }
		public int? Smg { get; set; }
		public int? Lmg { get; set; }
		public int? Pistol { get; set; }
		public string Rounds { get; set; } = "";
	}

	public async Task<SqliteConnection> OpenConnectionAsync()
	{
		SqliteConnection connection = new($"Data Source={DatabaseFilePath}");
		await connection.OpenAsync();
		await ApplyPragmasAsync(connection);

		return connection;
	}

	private static async Task ApplyPragmasAsync(SqliteConnection connection)
	{
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = "PRAGMA journal_mode='WAL';";
		await command.ExecuteNonQueryAsync();

		command.CommandText = "PRAGMA synchronous='NORMAL';";
		await command.ExecuteNonQueryAsync();

		command.CommandText = "PRAGMA busy_timeout=5000;";
		await command.ExecuteNonQueryAsync();
	}

	public async Task CreateTableAsync()
	{
		string tablePrefix = Config.DatabaseSettings.TablePrefix;
		string tableQuery = $@"
			CREATE TABLE IF NOT EXISTS ""{tablePrefix}k4-arenas"" (
				""steamid64"" TEXT PRIMARY KEY,
				""rifle"" INTEGER,
				""sniper"" INTEGER,
				""shotgun"" INTEGER,
				""smg"" INTEGER,
				""lmg"" INTEGER,
				""pistol"" INTEGER,
				""rounds"" TEXT NOT NULL,
				""lastseen"" TEXT NOT NULL
			);";

		using SqliteConnection connection = await OpenConnectionAsync();

		await _databaseWriteLock.WaitAsync();
		try
		{
			await connection.ExecuteAsync(tableQuery);
		}
		finally
		{
			_databaseWriteLock.Release();
		}
	}

	public async Task LoadPlayerAsync(ulong SteamID)
	{
		try
		{
			string tablePrefix = Config.DatabaseSettings.TablePrefix;
			DefaultWeaponSettings dws = Config.DefaultWeaponSettings;
			string steamId = SteamID.ToString();

			string sqlInsertOrUpdate = $@"
				INSERT INTO ""{tablePrefix}k4-arenas"" (""steamid64"", ""lastseen"", ""rifle"", ""sniper"", ""shotgun"", ""smg"", ""lmg"", ""pistol"", ""rounds"")
				VALUES (@SteamID, CURRENT_TIMESTAMP, @DefaultRifle, @DefaultSniper, @DefaultShotgun, @DefaultSMG, @DefaultLMG, @DefaultPistol, @Rounds)
				ON CONFLICT(""steamid64"") DO UPDATE SET ""lastseen"" = CURRENT_TIMESTAMP;";

			string sqlSelect = $@"
				SELECT ""rifle"", ""sniper"", ""shotgun"", ""smg"", ""lmg"", ""pistol"", ""rounds""
				FROM ""{tablePrefix}k4-arenas"" WHERE ""steamid64"" = @SteamID;";

			string rounds = string.Join(",", RoundType.RoundTypes.Where(r => r.EnabledByDefault).Select(x => x.ID.ToString()));

			using SqliteConnection connection = await OpenConnectionAsync();

			await _databaseWriteLock.WaitAsync();
			try
			{
				await connection.ExecuteAsync(sqlInsertOrUpdate, new
				{
					SteamID = steamId,
					Rounds = rounds,
					DefaultRifle = FindEnumValueByEnumMemberValue(dws.DefaultRifle),
					DefaultSniper = FindEnumValueByEnumMemberValue(dws.DefaultSniper),
					DefaultShotgun = FindEnumValueByEnumMemberValue(dws.DefaultShotgun),
					DefaultSMG = FindEnumValueByEnumMemberValue(dws.DefaultSMG),
					DefaultLMG = FindEnumValueByEnumMemberValue(dws.DefaultLMG),
					DefaultPistol = FindEnumValueByEnumMemberValue(dws.DefaultPistol)
				});
			}
			finally
			{
				_databaseWriteLock.Release();
			}

			PlayerPreferencesRow? result = await connection.QuerySingleOrDefaultAsync<PlayerPreferencesRow>(sqlSelect, new { SteamID = steamId });
			if (result != null)
			{
				ArenaPlayer? arenaPlayer = Arenas?.FindPlayer(SteamID);

				if (arenaPlayer == null)
					return;

				arenaPlayer.WeaponPreferences = new Dictionary<WeaponType, CsItem?>
				{
					{ WeaponType.Rifle, (CsItem?)result.Rifle },
					{ WeaponType.Sniper, (CsItem?)result.Sniper },
					{ WeaponType.Shotgun, (CsItem?)result.Shotgun },
					{ WeaponType.SMG, (CsItem?)result.Smg },
					{ WeaponType.LMG, (CsItem?)result.Lmg },
					{ WeaponType.Pistol, (CsItem?)result.Pistol }
				};

				if (!string.IsNullOrEmpty(result.Rounds))
				{
					List<int> validRoundIds = [];
					string[] roundIds = result.Rounds.Split(',');
					List<RoundType> roundPreferences = [];

					foreach (string roundId in roundIds)
					{
						if (int.TryParse(roundId, out int id))
						{
							RoundType? roundType = RoundType.RoundTypes.FirstOrDefault(x => x.ID == id);
							if (roundType != null)
							{
								roundPreferences.Add((RoundType)roundType);
								validRoundIds.Add(id);
							}
						}
					}

					if (validRoundIds.Count != roundIds.Length)
					{
						string validRounds = string.Join(",", validRoundIds);
						string sqlUpdateRounds = $@"
							UPDATE ""{tablePrefix}k4-arenas""
							SET ""rounds"" = @ValidRounds
							WHERE ""steamid64"" = @SteamID;";

						await _databaseWriteLock.WaitAsync();
						try
						{
							await connection.ExecuteAsync(sqlUpdateRounds, new { SteamID = steamId, ValidRounds = validRounds });
						}
						finally
						{
							_databaseWriteLock.Release();
						}
					}

					arenaPlayer.RoundPreferences = roundPreferences;

					arenaPlayer.Loaded = true;
				}
			}
		}
		catch (Exception ex)
		{
			Logger.LogError("Failed to load player preferences: {0}", ex.Message);
		}
	}

	public async Task SavePlayerPreferencesAsync(ArenaPlayer arenaPlayer)
	{
		if (!arenaPlayer.Loaded)
			return;

		try
		{
			string tablePrefix = Config.DatabaseSettings.TablePrefix;

			string sqlUpdate = $@"
				UPDATE ""{tablePrefix}k4-arenas""
				SET ""rifle"" = @Rifle, ""sniper"" = @Sniper, ""shotgun"" = @Shotgun, ""smg"" = @SMG, ""lmg"" = @LMG, ""pistol"" = @Pistol, ""rounds"" = @Rounds, ""lastseen"" = CURRENT_TIMESTAMP
				WHERE ""steamid64"" = @SteamID;";

			var weaponParameters = new
			{
				SteamID = arenaPlayer.SteamID.ToString(),
				Rifle = arenaPlayer.WeaponPreferences.TryGetValue(WeaponType.Rifle, out CsItem? rifle) ? rifle : null,
				Sniper = arenaPlayer.WeaponPreferences.TryGetValue(WeaponType.Sniper, out CsItem? sniper) ? sniper : null,
				Shotgun = arenaPlayer.WeaponPreferences.TryGetValue(WeaponType.Shotgun, out CsItem? shotgun) ? shotgun : null,
				SMG = arenaPlayer.WeaponPreferences.TryGetValue(WeaponType.SMG, out CsItem? smg) ? smg : null,
				LMG = arenaPlayer.WeaponPreferences.TryGetValue(WeaponType.LMG, out CsItem? lmg) ? lmg : null,
				Pistol = arenaPlayer.WeaponPreferences.TryGetValue(WeaponType.Pistol, out CsItem? pistol) ? pistol : null,
				Rounds = string.Join(",", arenaPlayer.RoundPreferences.Select(r => r.ID))
			};

			using SqliteConnection connection = await OpenConnectionAsync();

			await _databaseWriteLock.WaitAsync();
			try
			{
				await connection.ExecuteAsync(sqlUpdate, weaponParameters);
			}
			finally
			{
				_databaseWriteLock.Release();
			}
		}
		catch (Exception ex)
		{
			// Logged only: this method always runs fire-and-forget via Task.Run(), so a rethrow here
			// would just become an unobserved task exception instead of surfacing anywhere useful.
			Logger.LogError("Failed to save player preferences: {0}", ex.Message);
		}
	}

	public async Task PurgeDatabaseAsync()
	{
		if (Config.DatabaseSettings.TablePurgeDays <= 0)
			return;

		string tablePrefix = Config.DatabaseSettings.TablePrefix;
		string query = $@"
			DELETE FROM ""{tablePrefix}k4-arenas""
			WHERE datetime(""lastseen"") < datetime('now', '-' || @PurgeDays || ' days');";

		using SqliteConnection connection = await OpenConnectionAsync();

		await _databaseWriteLock.WaitAsync();
		try
		{
			await connection.ExecuteAsync(query, new { PurgeDays = Config.DatabaseSettings.TablePurgeDays });
		}
		finally
		{
			_databaseWriteLock.Release();
		}
	}
}
