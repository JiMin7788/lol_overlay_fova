using System.Text;
using System.Text.Json;

namespace Overlay.Core;

/// <summary>
/// Zero-allocation parser for the Live Client Data API <c>allgamedata</c> payload.
///
/// Parses raw UTF-8 response bytes with <see cref="Utf8JsonReader"/> over a
/// <see cref="ReadOnlySpan{T}"/> straight off the pooled receive buffer — no
/// JsonDocument, no DOM, no LINQ, no boxing (Hard Rule #2). Values are written
/// directly into a caller-supplied reusable <see cref="GameSnapshot"/>.
///
/// The only managed allocations are the identity strings (summoner / champion
/// names) that downstream consumers genuinely need; everything numeric is read
/// straight off the span.
/// </summary>
public static class LiveDataParser
{
    /// <summary>
    /// Parse <paramref name="utf8Json"/> into <paramref name="snapshot"/> (which
    /// is Reset() first). Returns true if a usable game state was parsed.
    /// </summary>
    public static bool Parse(ReadOnlySpan<byte> utf8Json, GameSnapshot snapshot)
    {
        snapshot.ResetForParse();
        if (utf8Json.IsEmpty) return false;

        var reader = new Utf8JsonReader(utf8Json, isFinalBlock: true, state: default);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("activePlayer"u8))
                ReadActivePlayer(ref reader, snapshot);
            else if (reader.ValueTextEquals("allPlayers"u8))
                ReadAllPlayers(ref reader, snapshot);
            else if (reader.ValueTextEquals("events"u8))
                ReadEvents(ref reader, snapshot);
            else if (reader.ValueTextEquals("gameData"u8))
                ReadGameData(ref reader, snapshot);
            else
                reader.Skip();
        }

        snapshot.HasData = true;
        return true;
    }

    /// <summary>
    /// Reads a JSON string value at the reader's current position and assigns it
    /// to <paramref name="existing"/> only if it differs from the bytes already
    /// there. In steady state (identity names unchanged tick-to-tick) this does a
    /// pure byte comparison and allocates nothing — satisfying Hard Rule #2.
    /// </summary>
    private static string ReadStringIfChanged(ref Utf8JsonReader reader, string existing)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return string.Empty;
        // ValueTextEquals compares the raw UTF-8 token against the existing string
        // without materializing a new string. Only allocate on an actual change.
        return reader.ValueTextEquals(existing) ? existing : (reader.GetString() ?? string.Empty);
    }

    private static void ReadActivePlayer(ref Utf8JsonReader reader, GameSnapshot s)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); return; }

        int depth = reader.CurrentDepth;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("summonerName"u8))
            {
                reader.Read();
                s.ActivePlayerSummonerName = ReadStringIfChanged(ref reader, s.ActivePlayerSummonerName);
            }
            else if (reader.ValueTextEquals("riotId"u8))
            {
                reader.Read();
                s.ActivePlayerRiotId = ReadStringIfChanged(ref reader, s.ActivePlayerRiotId);
            }
            else if (reader.ValueTextEquals("currentGold"u8))
            {
                reader.Read();
                s.CurrentGold = reader.GetDouble();
            }
            else if (reader.ValueTextEquals("level"u8))
            {
                reader.Read();
                s.Level = reader.GetInt32();
            }
            else if (reader.ValueTextEquals("championStats"u8))
            {
                ReadChampionStats(ref reader, s);
            }
            else if (reader.ValueTextEquals("abilities"u8))
            {
                ReadAbilities(ref reader, s);
            }
            else if (reader.ValueTextEquals("fullRunes"u8))
            {
                ReadFullRunes(ref reader, s);
            }
            else
            {
                reader.Read();
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    reader.Skip();
            }
        }
    }

    private static void ReadChampionStats(ref Utf8JsonReader reader, GameSnapshot s)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); return; }

        int depth = reader.CurrentDepth;
        ref ActivePlayerStats st = ref s.Stats;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("currentHealth"u8)) { reader.Read(); st.CurrentHealth = reader.GetDouble(); }
            else if (reader.ValueTextEquals("maxHealth"u8)) { reader.Read(); st.MaxHealth = reader.GetDouble(); }
            else if (reader.ValueTextEquals("resourceValue"u8)) { reader.Read(); st.ResourceValue = reader.GetDouble(); }
            else if (reader.ValueTextEquals("resourceMax"u8)) { reader.Read(); st.ResourceMax = reader.GetDouble(); }
            else if (reader.ValueTextEquals("attackDamage"u8)) { reader.Read(); st.AttackDamage = reader.GetDouble(); }
            else if (reader.ValueTextEquals("abilityPower"u8)) { reader.Read(); st.AbilityPower = reader.GetDouble(); }
            else if (reader.ValueTextEquals("armor"u8)) { reader.Read(); st.Armor = reader.GetDouble(); }
            else if (reader.ValueTextEquals("magicResist"u8)) { reader.Read(); st.MagicResist = reader.GetDouble(); }
            else if (reader.ValueTextEquals("moveSpeed"u8)) { reader.Read(); st.MoveSpeed = reader.GetDouble(); }
            // Final attacks-per-second (activePlayer.championStats.attackSpeed, confirmed in Riot's
            // liveclientdata_sample.json). Consumed by SkillDamage's mStat=4 resolver mapping (§48).
            else if (reader.ValueTextEquals("attackSpeed"u8)) { reader.Read(); st.AttackSpeed = reader.GetDouble(); }
            // Penetration flat fields: additive, no unit ambiguity.
            else if (reader.ValueTextEquals("armorPenetrationFlat"u8)) { reader.Read(); st.ArmorPenetrationFlat = reader.GetDouble(); }
            else if (reader.ValueTextEquals("magicPenetrationFlat"u8)) { reader.Read(); st.MagicPenetrationFlat = reader.GetDouble(); }
            // Percent penetration fields — loop-38 fix (CONFIRMED live, was a flagged/unverified
            // assumption): the Live Client API reports these as a MULTIPLIER on remaining
            // resistance (baseline "no bonus pen" = 1.0, i.e. "100% of resistance still applies"),
            // NOT as an additive 0-1 bonus fraction (where baseline would be 0.0) the way
            // DamageEngine.EffectiveResistMultiplier's `1.0 - penPercent` expects. Reading the raw
            // API value directly made a naked, zero-pen-item attacker's `penPercent` come through as
            // 1.0 -> `r *= (1.0 - 1.0) = 0` -> resistance ALWAYS zeroed regardless of the target's
            // real armor/MR, for every damage type -> every hit rendered as unmitigated/true-damage-
            // looking. Root-caused via live evidence: a user confirmed (a) the resist readout showed
            // real nonzero target values, (b) the executed node's DamageType tag was correct (Magic
            // for a spell, hardcoded Physical for an auto-attack), yet (c) damage exactly matched the
            // raw ability tooltip in every test — the only remaining explanation was this exact unit
            // mismatch, since it independently zeroes EffectiveResistMultiplier's percent-pen term
            // for physical AND magic alike, with no target-side signal to distinguish it (this is why
            // continuations 1-9 in M05's Pending section, which all assumed the DEFENDER side was at
            // fault, never found it). FIX: convert Riot's multiplier into our additive-fraction
            // convention at the parse boundary (`1.0 - raw`), so a baseline 1.0 correctly becomes our
            // 0.0 (no bonus pen) and every downstream consumer (AttackerStat, EffectiveResistMultiplier)
            // needs no further change.
            else if (reader.ValueTextEquals("armorPenetrationPercent"u8)) { reader.Read(); st.ArmorPenetrationPercent = 1.0 - reader.GetDouble(); }
            else if (reader.ValueTextEquals("magicPenetrationPercent"u8)) { reader.Read(); st.MagicPenetrationPercent = 1.0 - reader.GetDouble(); }
            // Crit stats (M05 v2.8, real-crit-range simplification): field names confirmed
            // verbatim against Riot's public Live Client Data API sample JSON — no unit-flip
            // needed here (unlike armorPenetrationPercent/magicPenetrationPercent above),
            // critChance is already the 0-1 fraction AttackerStat.CriticalChance expects.
            else if (reader.ValueTextEquals("critChance"u8)) { reader.Read(); st.CritChance = reader.GetDouble(); }
            else if (reader.ValueTextEquals("critDamage"u8)) { reader.Read(); st.CritDamage = reader.GetDouble(); }
            else { reader.Read(); if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip(); }
        }
    }

    /// <summary>Reads <c>activePlayer.abilities</c> — a nested object keyed by slot
    /// ("Q"/"W"/"E"/"R"/"Passive"), each an object carrying an <c>abilityLevel</c> int —
    /// into the snapshot's real ability ranks. Mirrors <see cref="ReadChampionStats"/>'s
    /// depth handling (break only on the matching-depth EndObject). Passive / any unknown
    /// slot is skipped; an absent <c>abilities</c> block simply leaves ranks at 0.</summary>
    private static void ReadAbilities(ref Utf8JsonReader reader, GameSnapshot s)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); return; }

        int depth = reader.CurrentDepth;
        ref ActivePlayerStats st = ref s.Stats;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("Q"u8)) st.AbilityQ = ReadAbilityLevel(ref reader);
            else if (reader.ValueTextEquals("W"u8)) st.AbilityW = ReadAbilityLevel(ref reader);
            else if (reader.ValueTextEquals("E"u8)) st.AbilityE = ReadAbilityLevel(ref reader);
            else if (reader.ValueTextEquals("R"u8)) st.AbilityR = ReadAbilityLevel(ref reader);
            else { reader.Read(); if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip(); }
        }
    }

    /// <summary>Reads a single ability object (the value of a Q/W/E/R property) and returns
    /// its <c>abilityLevel</c>, fully consuming the object so the caller's loop resumes at
    /// the next slot. Returns 0 if the value isn't an object or has no <c>abilityLevel</c>.</summary>
    private static int ReadAbilityLevel(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); return 0; }

        int depth = reader.CurrentDepth;
        int level = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("abilityLevel"u8)) { reader.Read(); level = reader.GetInt32(); }
            else { reader.Read(); if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip(); }
        }
        return level;
    }

    /// <summary>Reads <c>activePlayer.fullRunes</c> into <see cref="ActivePlayerStats.EquippedRuneIds"/>:
    /// the primary tree's <c>keystone.id</c> plus every id in <c>generalRunes[]</c> (deduplicated —
    /// Riot repeats the keystone as generalRunes[0] in practice). <c>statRunes</c> (stat shards) is a
    /// sibling field that is simply never read here, so shards can never leak into the rune-id list.
    /// Leaves the snapshot's existing empty-array default (see <see cref="GameSnapshot.ResetForParse"/>)
    /// untouched if this object is empty/malformed — never throws, never fabricates an id.</summary>
    private static void ReadFullRunes(ref Utf8JsonReader reader, GameSnapshot s)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); return; }

        int depth = reader.CurrentDepth;
        var ids = new List<int>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("keystone"u8))
            {
                int id = ReadRuneObjectId(ref reader);
                if (id != 0 && !ids.Contains(id)) ids.Add(id);
            }
            else if (reader.ValueTextEquals("generalRunes"u8))
            {
                ReadGeneralRunes(ref reader, ids);
            }
            else { reader.Read(); if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip(); }
        }

        if (ids.Count > 0) s.Stats.EquippedRuneIds = ids;
    }

    /// <summary>Reads <c>generalRunes</c> — an array of <c>{id, rawDescription, rawDisplayName}</c>
    /// objects, one per equipped non-shard rune — appending each distinct id to <paramref name="ids"/>.</summary>
    private static void ReadGeneralRunes(ref Utf8JsonReader reader, List<int> ids)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); return; }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) break;
            if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }

            int depth = reader.CurrentDepth;
            int id = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                if (reader.ValueTextEquals("id"u8)) { reader.Read(); id = reader.GetInt32(); }
                else { reader.Read(); if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip(); }
            }
            if (id != 0 && !ids.Contains(id)) ids.Add(id);
        }
    }

    /// <summary>Reads a single <c>{id, rawDescription, rawDisplayName}</c> rune object (the value of
    /// the <c>keystone</c> property) and returns its <c>id</c>, fully consuming the object. Mirrors
    /// <see cref="ReadAbilityLevel"/>'s pattern. Returns 0 if the value isn't an object or has no id.</summary>
    private static int ReadRuneObjectId(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); return 0; }

        int depth = reader.CurrentDepth;
        int id = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("id"u8)) { reader.Read(); id = reader.GetInt32(); }
            else { reader.Read(); if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip(); }
        }
        return id;
    }

    private static void ReadAllPlayers(ref Utf8JsonReader reader, GameSnapshot s)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); return; }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) break;
            if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }

            if (s.PlayerCount >= s.Players.Length)
            {
                // Defensive: more players than the fixed buffer — skip the rest.
                reader.Skip();
                continue;
            }

            ScoreboardEntry entry = s.Players[s.PlayerCount];
            entry.ResetForParse(); // keep prior identity strings for byte-compare
            ReadPlayer(ref reader, entry);
            s.PlayerCount++;
        }
    }

    private static void ReadPlayer(ref Utf8JsonReader reader, ScoreboardEntry e)
    {
        int depth = reader.CurrentDepth;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("summonerName"u8)) { reader.Read(); e.SummonerName = ReadStringIfChanged(ref reader, e.SummonerName); }
            else if (reader.ValueTextEquals("riotId"u8)) { reader.Read(); e.RiotId = ReadStringIfChanged(ref reader, e.RiotId); }
            else if (reader.ValueTextEquals("championName"u8)) { reader.Read(); e.ChampionName = ReadStringIfChanged(ref reader, e.ChampionName); }
            else if (reader.ValueTextEquals("team"u8)) { reader.Read(); e.Team = ReadStringIfChanged(ref reader, e.Team); }
            // Best-effort lane (ranked/draft only; "" otherwise). activePlayer has no position field,
            // so the active player's position is derived by matching its scoreboard row (ComboRunner).
            else if (reader.ValueTextEquals("position"u8)) { reader.Read(); e.Position = ReadStringIfChanged(ref reader, e.Position); }
            else if (reader.ValueTextEquals("level"u8)) { reader.Read(); e.Level = reader.GetInt32(); }
            else if (reader.ValueTextEquals("isDead"u8)) { reader.Read(); e.IsDead = reader.TokenType == JsonTokenType.True; }
            else if (reader.ValueTextEquals("respawnTimer"u8)) { reader.Read(); e.RespawnTimer = reader.GetDouble(); }
            else if (reader.ValueTextEquals("scores"u8)) ReadScores(ref reader, e);
            else if (reader.ValueTextEquals("items"u8)) ReadItems(ref reader, e);
            // M31 P3 (Smite jungler-ID fallback, unlike position/ available in every game mode).
            else if (reader.ValueTextEquals("summonerSpells"u8)) ReadSummonerSpells(ref reader, e);
            else { reader.Read(); if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip(); }
        }
    }

    /// <summary>allPlayers[].summonerSpells: { summonerSpellOne: { rawDisplayName }, summonerSpellTwo:
    /// { rawDisplayName } }. Only <c>rawDisplayName</c> is captured (e.g. "SummonerSmite") — the
    /// stable internal name, not the localized <c>displayName</c>.</summary>
    private static void ReadSummonerSpells(ref Utf8JsonReader reader, ScoreboardEntry e)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); return; }

        int depth = reader.CurrentDepth;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("summonerSpellOne"u8))
                e.Spell1RawName = ReadRawDisplayName(ref reader, e.Spell1RawName);
            else if (reader.ValueTextEquals("summonerSpellTwo"u8))
                e.Spell2RawName = ReadRawDisplayName(ref reader, e.Spell2RawName);
            else { reader.Read(); if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip(); }
        }
    }

    private static string ReadRawDisplayName(ref Utf8JsonReader reader, string existing)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); return existing; }

        string result = existing;
        int depth = reader.CurrentDepth;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("rawDisplayName"u8)) { reader.Read(); result = ReadStringIfChanged(ref reader, result); }
            else { reader.Read(); if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip(); }
        }
        return result;
    }

    private static void ReadScores(ref Utf8JsonReader reader, ScoreboardEntry e)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); return; }

        int depth = reader.CurrentDepth;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("kills"u8)) { reader.Read(); e.Kills = reader.GetInt32(); }
            else if (reader.ValueTextEquals("deaths"u8)) { reader.Read(); e.Deaths = reader.GetInt32(); }
            else if (reader.ValueTextEquals("assists"u8)) { reader.Read(); e.Assists = reader.GetInt32(); }
            else if (reader.ValueTextEquals("creepScore"u8)) { reader.Read(); e.CreepScore = reader.GetInt32(); }
            else { reader.Read(); if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip(); }
        }
    }

    private static void ReadItems(ref Utf8JsonReader reader, ScoreboardEntry e)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); return; }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) break;
            if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }

            int depth = reader.CurrentDepth;
            int itemId = 0;
            int count = 1;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                if (reader.ValueTextEquals("itemID"u8)) { reader.Read(); itemId = reader.GetInt32(); }
                else if (reader.ValueTextEquals("count"u8)) { reader.Read(); count = reader.GetInt32(); }
                else { reader.Read(); if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip(); }
            }
            e.TryAddItem(itemId, count);
        }
    }

    private static void ReadEvents(ref Utf8JsonReader reader, GameSnapshot s)
    {
        // events: { "Events": [ { "EventID": n, ... }, ... ] }
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); return; }

        int depth = reader.CurrentDepth;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("Events"u8))
                ReadEventArray(ref reader, s);
            else { reader.Read(); if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip(); }
        }
    }

    private static void ReadEventArray(ref Utf8JsonReader reader, GameSnapshot s)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); return; }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) break;
            if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }

            s.EventCount++;
            int depth = reader.CurrentDepth;

            // Captured opportunistically as the object's fields stream past; Riot
            // emits "EventID" before "EventName" in practice, so by the time we know
            // this is a ChampionKill (M01 gap #5), we already have the id to seed the
            // slot with. `slot` stays null for every non-ChampionKill event, so this
            // adds no extra work/allocation to the far more common event types.
            // `inhibSlot` is the same idea for InhibKilled (M19 §3.2 parser extension).
            int eventId = 0;
            KillEventRecord? slot = null;
            InhibEventRecord? inhibSlot = null;
            TurretEventRecord? turretSlot = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                if (reader.ValueTextEquals("EventID"u8))
                {
                    reader.Read();
                    eventId = reader.GetInt32();
                    if (eventId > s.MaxEventId) s.MaxEventId = eventId;
                    if (slot is not null) slot.EventId = eventId;
                    if (inhibSlot is not null) inhibSlot.EventId = eventId;
                    if (turretSlot is not null) turretSlot.EventId = eventId;
                }
                else if (reader.ValueTextEquals("EventName"u8))
                {
                    reader.Read();
                    if (reader.ValueTextEquals("ChampionKill"u8) && s.KillEventCount < s.KillEvents.Length)
                    {
                        slot = s.KillEvents[s.KillEventCount];
                        slot.IsChampionKill = true;
                        slot.EventId = eventId;
                    }
                    else if (reader.ValueTextEquals("InhibKilled"u8) && s.InhibEventCount < s.InhibEvents.Length)
                    {
                        inhibSlot = s.InhibEvents[s.InhibEventCount];
                        inhibSlot.IsInhibKilled = true;
                        inhibSlot.EventId = eventId;
                    }
                    else if (reader.ValueTextEquals("InhibRespawned"u8) && s.InhibEventCount < s.InhibEvents.Length)
                    {
                        inhibSlot = s.InhibEvents[s.InhibEventCount];
                        inhibSlot.IsInhibRespawned = true;
                        inhibSlot.EventId = eventId;
                    }
                    else if (reader.ValueTextEquals("TurretKilled"u8) && s.TurretEventCount < s.TurretEvents.Length)
                    {
                        turretSlot = s.TurretEvents[s.TurretEventCount];
                        turretSlot.IsTurretKilled = true;
                        turretSlot.EventId = eventId;
                    }
                }
                else if (reader.ValueTextEquals("EventTime"u8))
                {
                    reader.Read();
                    double t = reader.GetDouble();
                    if (slot is not null) slot.EventTime = t;
                    if (inhibSlot is not null) inhibSlot.EventTime = t;
                    if (turretSlot is not null) turretSlot.EventTime = t;
                }
                else if (reader.ValueTextEquals("KillerName"u8))
                {
                    reader.Read();
                    if (slot is not null) slot.KillerName = ReadStringIfChanged(ref reader, slot.KillerName);
                    if (inhibSlot is not null) inhibSlot.KillerName = ReadStringIfChanged(ref reader, inhibSlot.KillerName);
                    if (turretSlot is not null) turretSlot.KillerName = ReadStringIfChanged(ref reader, turretSlot.KillerName);
                }
                else if (reader.ValueTextEquals("VictimName"u8))
                {
                    reader.Read();
                    if (slot is not null) slot.VictimName = ReadStringIfChanged(ref reader, slot.VictimName);
                }
                else if (reader.ValueTextEquals("InhibKilled"u8))
                {
                    // The field literally named "InhibKilled" (distinct from the EventName
                    // property's value of the same text checked above): the target
                    // inhibitor's raw id, e.g. "Barracks_T1_L1".
                    reader.Read();
                    if (inhibSlot is not null) inhibSlot.InhibId = ReadStringIfChanged(ref reader, inhibSlot.InhibId);
                }
                else if (reader.ValueTextEquals("InhibRespawned"u8))
                {
                    // Field literally named "InhibRespawned": the respawned inhibitor's raw id
                    // (same value shape as InhibKilled, e.g. "Barracks_T1_L1").
                    reader.Read();
                    if (inhibSlot is not null) inhibSlot.InhibId = ReadStringIfChanged(ref reader, inhibSlot.InhibId);
                }
                else if (reader.ValueTextEquals("TurretKilled"u8))
                {
                    // The field literally named "TurretKilled" (distinct from the EventName
                    // property's value checked above): the destroyed turret's raw id, e.g.
                    // "Turret_T1_C_07_A". Format unconfirmed vs live game (see TurretEventRecord).
                    reader.Read();
                    if (turretSlot is not null) turretSlot.TurretId = ReadStringIfChanged(ref reader, turretSlot.TurretId);
                }
                else { reader.Read(); if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip(); }
            }

            if (slot is not null) s.KillEventCount++;
            if (inhibSlot is not null) s.InhibEventCount++;
            if (turretSlot is not null) s.TurretEventCount++;
        }
    }

    private static void ReadGameData(ref Utf8JsonReader reader, GameSnapshot s)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); return; }

        int depth = reader.CurrentDepth;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("gameTime"u8)) { reader.Read(); s.GameTime = reader.GetDouble(); }
            else { reader.Read(); if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip(); }
        }
    }
}
