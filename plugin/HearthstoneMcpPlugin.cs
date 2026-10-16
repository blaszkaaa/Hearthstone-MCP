using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using Hearthstone_Deck_Tracker.Plugins;
using Newtonsoft.Json;

namespace HearthstoneMcpPlugin
{
    /// <summary>
    /// Read-only HDT plugin. Exports only the player's visible game state, never
    /// enemy hand/deck identities. No game input, hooks, or injected game code.
    /// </summary>
    public sealed class HearthstoneMcpPlugin : IPlugin
    {
        private bool _enabled;
        private DateTime _lastWriteUtc = DateTime.MinValue;
        private static readonly TimeSpan UpdateEvery = TimeSpan.FromMilliseconds(500);
        private static readonly object LogLock = new object();
        private string _lastPhaseSignature;

        public string Name => "Hearthstone MCP";
        public string Description => "Udostepnia stan meczu i aktywne wybory kart (Odkryj / Choose One) do MCP.";
        public string ButtonText => "Pokaz plik danych";
        public string Author => "Hearthstone-MCP contributors";
        public Version Version => new Version(0, 5, 0);
        public MenuItem MenuItem => null;

        private static string StatePath
        {
            get
            {
                var overridePath = Environment.GetEnvironmentVariable("HEARTHSTONE_MCP_STATE_PATH");
                return !string.IsNullOrWhiteSpace(overridePath)
                    ? overridePath
                    : Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "HearthstoneMCP", "state.json");
            }
        }

        public void OnLoad()
        {
            _enabled = true;
            _lastWriteUtc = DateTime.MinValue;
            TryDiagnostic("Plugin uruchomiony v0.5");
            OnUpdate();
        }

        public void OnUnload()
        {
            _enabled = false;
            try { WriteSnapshot(false); TryDiagnostic("Wtyczka wylaczona"); } catch (Exception ex) { TryDiagnostic("OnUnload " + ex); }
        }

        public void OnButtonPress()
        {
            MessageBox.Show("Plik stanu Hearthstone MCP:\n" + StatePath,
                "Hearthstone MCP", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public void OnUpdate()
        {
            if (!_enabled || DateTime.UtcNow - _lastWriteUtc < UpdateEvery)
                return;
            _lastWriteUtc = DateTime.UtcNow;
            try { WriteSnapshot(true); }
            catch (Exception ex) { TryDiagnostic("WriteSnapshot: " + ex); }
        }

        private static int? LiveTorchDamage(Entity entity)
        {
            if (!string.Equals(entity.CardId, "CATA_585", StringComparison.OrdinalIgnoreCase)) return null;
            // Resolve by name so older HearthDb versions fail safely instead of
            // requiring an enum constant they might not expose at compile time.
            if (!Enum.TryParse("TAG_SCRIPT_DATA_NUM_1", out GameTag liveTag)) return null;
            int value = entity.GetTag(liveTag);
            return value > 0 && value <= 8 ? (int?)value : null;
        }

        private static object AsCard(Entity entity, bool withCombatData, bool ownOfferedChoice = false)
        {
            if (entity == null)
                return null;

            // Do not access opponent's hidden card identities. This helper is only
            // called for own hand and public board / played cards.
            if ((entity.Info.Hidden && !ownOfferedChoice) || !entity.HasCardId)
                return new { entity_id = entity.Id, known = false, position = entity.ZonePosition };

            return new
            {
                entity_id = entity.Id,
                known = true,
                card_id = entity.CardId,
                // HearthDb printed metadata is authoritative for the installed
                // tracker DB revision, not for live buffs or changed costs.
                card_dbf_id = entity.Card.DbfId > 0 ? (int?)entity.Card.DbfId : null,
                printed_mechanics = (entity.Card.Mechanics ?? Array.Empty<string>()).Take(24).ToArray(),
                printed_data_source = "HearthDb",
                // HDT/HearthDb knows card kind. The client MUST NOT decide
                // whether a card can attack from its name or printed description.
                card_type = entity.GetTag(GameTag.CARDTYPE) > 0
                    ? ((CardType)entity.GetTag(GameTag.CARDTYPE)).ToString()
                    : entity.Card.Type.ToString(),
                name = entity.LocalizedName ?? entity.Name ?? entity.CardId,
                card_text = entity.Card.Text,
                card_text_en = entity.Card.EnglishText,
                // The mutable game-entity script tag tracks remaining Torch damage.
                // Null means HDT cannot confirm it; never substitute printed 8.
                live_damage = LiveTorchDamage(entity),
                live_damage_source = string.Equals(entity.CardId, "CATA_585", StringComparison.OrdinalIgnoreCase)
                    ? "TAG_SCRIPT_DATA_NUM_1" : null,
                position = entity.ZonePosition,
                cost = entity.Cost,
                attack = withCombatData ? (int?)entity.Attack : null,
                health = withCombatData ? (int?)entity.Health : null,
                taunt = withCombatData ? (bool?)(entity.GetTag(GameTag.TAUNT) > 0) : null,
                divine_shield = withCombatData ? (bool?)(entity.GetTag(GameTag.DIVINE_SHIELD) > 0) : null,
                exhausted = withCombatData ? (bool?)(entity.GetTag(GameTag.EXHAUSTED) > 0) : null,
                frozen = withCombatData ? (bool?)(entity.GetTag(GameTag.FROZEN) > 0) : null,
            };
        }

        private static object AsHero(Entity hero)
        {
            if (hero == null)
                return null;
            return new
            {
                card_id = hero.CardId,
                name = hero.LocalizedName ?? hero.Name,
                health = hero.Health,
                armor = hero.GetTag(GameTag.ARMOR),
                attack = hero.Attack,
                frozen = hero.GetTag(GameTag.FROZEN) > 0,
            };
        }

        private static object AsHeroPower(Entity power)
        {
            if (power == null)
                return null;
            // Only a visible identity and its observed cost, NOT a claim that the
            // action is still usable this turn.
            return new
            {
                card_id = power.HasCardId && !power.Info.Hidden ? power.CardId : null,
                name = power.HasCardId && !power.Info.Hidden
                    ? (power.LocalizedName ?? power.Name ?? power.CardId) : null,
                cost = power.Cost,
                card_text = power.HasCardId && !power.Info.Hidden ? power.Card.Text : null,
                card_text_en = power.HasCardId && !power.Info.Hidden ? power.Card.EnglishText : null,
                card_type = power.HasCardId && !power.Info.Hidden ? power.Card.Type.ToString() : null,
                card_dbf_id = power.HasCardId && !power.Info.Hidden && power.Card.DbfId > 0
                    ? (int?)power.Card.DbfId : null,
                printed_mechanics = power.HasCardId && !power.Info.Hidden
                    ? (power.Card.Mechanics ?? Array.Empty<string>()).Take(24).ToArray() : null,
                printed_data_source = power.HasCardId && !power.Info.Hidden ? "HearthDb" : null,
            };
        }

        private static object AsWeapon(Entity weapon)
        {
            if (weapon == null)
                return null;
            int durability = weapon.GetTag(GameTag.DURABILITY);
            int damage = weapon.GetTag(GameTag.DAMAGE);
            return new
            {
                card_id = weapon.HasCardId && !weapon.Info.Hidden ? weapon.CardId : null,
                name = weapon.HasCardId && !weapon.Info.Hidden
                    ? (weapon.LocalizedName ?? weapon.Name ?? weapon.CardId) : null,
                attack = weapon.Attack,
                durability_remaining = durability > 0 ? (int?)Math.Max(0, durability - damage) : null,
                card_text = weapon.HasCardId && !weapon.Info.Hidden ? weapon.Card.Text : null,
                card_text_en = weapon.HasCardId && !weapon.Info.Hidden ? weapon.Card.EnglishText : null,
                card_type = weapon.HasCardId && !weapon.Info.Hidden ? weapon.Card.Type.ToString() : null,
                card_dbf_id = weapon.HasCardId && !weapon.Info.Hidden && weapon.Card.DbfId > 0
                    ? (int?)weapon.Card.DbfId : null,
                printed_mechanics = weapon.HasCardId && !weapon.Info.Hidden
                    ? (weapon.Card.Mechanics ?? Array.Empty<string>()).Take(24).ToArray() : null,
                printed_data_source = weapon.HasCardId && !weapon.Info.Hidden ? "HearthDb" : null,
            };
        }

        private static object[] BoardCards(System.Collections.Generic.IEnumerable<Entity> board)
            => board.Where(e => e.IsMinion || e.IsLocation)
                    .OrderBy(e => e.ZonePosition)
                    .Select(e => AsCard(e, true)).ToArray();

        private static string[] PublicPlays(System.Collections.Generic.IEnumerable<Entity> cards)
            => cards.Where(e => e.HasCardId && !e.Info.Hidden)
                    .Select(e => e.LocalizedName ?? e.Name ?? e.CardId)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Take(30).ToArray();

        private static void TryDiagnostic(string text)
        {
            try
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HearthstoneMCP");
                lock(LogLock)
                {
                    Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder, "plugin.log");
                    if(File.Exists(path) && new FileInfo(path).Length > 4 * 1024 * 1024)
                    {
                        if(File.Exists(path + ".old")) File.Delete(path + ".old");
                        File.Move(path, path + ".old");
                    }
                    File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + text + Environment.NewLine);
                }
            }
            catch(Exception) { /* Never fail the HDT plugin because of diagnostics. */ }
        }

        private void WriteSnapshot(bool allowGame)
        {
            var game = Core.Game;
            var inGame = allowGame && game != null && !game.IsInMenu
                && game.Player.Id > 0 && game.Opponent.Id > 0;
            object output;

            if (!inGame)
            {
                output = new
                {
                    schema_version = 1,
                    captured_at_utc = DateTime.UtcNow.ToString("o"),
                    source = "hearthstone-deck-tracker-plugin",
                    in_game = false,
                };
            }
            else
            {
                var player = game.Player;
                var opponent = game.Opponent;
                var playerEntity = game.PlayerEntity;
                int? manaMax = playerEntity == null ? (int?)null
                    : playerEntity.GetTag(GameTag.RESOURCES);
                int? manaAvailable = playerEntity == null ? (int?)null
                    : Math.Max(0, playerEntity.GetTag(GameTag.RESOURCES)
                        + playerEntity.GetTag(GameTag.TEMP_RESOURCES)
                        - playerEntity.GetTag(GameTag.RESOURCES_USED));

                // HDT stores the player's currently offered choices here.
                // The list gets cleared when the player chooses an option.
                // Index is the visual offer order; ZonePosition is not reliable
                // for these choice entities, which are usually SETASIDE.
                var offeredIds = player.OfferedEntityIds == null
                    ? Array.Empty<int>()
                    : player.OfferedEntityIds.Take(20).ToArray();
                var choices = offeredIds.Select((id, index) =>
                {
                    Entity offered;
                    object card = game.Entities.TryGetValue(id, out offered)
                        ? AsCard(offered, false, true)
                        : new { entity_id = id, known = false };
                    return new
                    {
                        index = index + 1,
                        card = card,
                    };
                }).ToArray();

                // Do not keep offering mulligan advice after the local player
                // already confirmed, even if the opponent is still choosing.
                bool mulligan = !game.IsMulliganDone &&
                    (playerEntity == null ||
                     playerEntity.GetTag(GameTag.MULLIGAN_STATE) != (int)HearthDb.Enums.Mulligan.DONE);
                var mulliganCards = mulligan ? player.Hand.Where(e => !e.IsTheCoin)
                    .OrderBy(e => e.ZonePosition).Select(e => AsCard(e, false)).ToArray()
                    : Array.Empty<object>();

                string phase = mulligan ? "mulligan" : offeredIds.Length > 0 ? "choice" : "turn";
                string signature = phase + " t=" + game.GetTurnNumber()
                    + " mine=" + (playerEntity != null && playerEntity.GetTag(GameTag.CURRENT_PLAYER) == 1)
                    + " mulligan=" + mulliganCards.Length + " offered=" + offeredIds.Length
                    + " hand=" + player.Hand.Count();
                if(signature != _lastPhaseSignature)
                {
                    _lastPhaseSignature = signature;
                    TryDiagnostic("Stan gry: " + signature);
                }
                output = new
                {
                    schema_version = 1,
                    captured_at_utc = DateTime.UtcNow.ToString("o"),
                    source = "hearthstone-deck-tracker-plugin",
                    in_game = true,
                    card_evidence_schema = 2,
                    card_catalog_source = "HearthDb (installed HDT)",
                    card_catalog_assembly_version = typeof(HearthDb.Card).Assembly.GetName().Version?.ToString(),
                    phase = phase,
                    mulligan_active = mulligan,
                    mulligan_cards = mulliganCards,
                    turn = game.GetTurnNumber(),
                    my_turn = playerEntity != null && playerEntity.GetTag(GameTag.CURRENT_PLAYER) == 1,
                    // Choice types include Discover, Choose One and other
                    // decision prompts. This is not necessarily Discover.
                    choice_tracking_supported = true,
                    card_types_supported = true,
                    choice_active = !mulligan && offeredIds.Length > 0,
                    choice_count = offeredIds.Length,
                    choices = mulligan ? Array.Empty<object>() : choices,
                    player = new
                    {
                        card_class = player.CurrentClass,
                        mana_max = manaMax,
                        mana_available = manaAvailable,
                        hand = player.Hand.OrderBy(e => e.ZonePosition)
                            .Select(e => AsCard(e, false)).ToArray(),
                        deck_count = player.DeckCount,
                        hero = AsHero(player.Hero),
                        hero_power = AsHeroPower(player.Board.FirstOrDefault(e => e.IsHeroPower)),
                        weapon = AsWeapon(player.Board.FirstOrDefault(e => e.IsWeapon)),
                        board = BoardCards(player.Board),
                        secrets_count = player.Secrets.Count(),
                        played_this_turn = PublicPlays(player.CardsPlayedThisTurn),
                    },
                    opponent = new
                    {
                        card_class = opponent.CurrentClass,
                        hand_count = opponent.Hand.Count(),
                        deck_count = opponent.DeckCount,
                        hero = AsHero(opponent.Hero),
                        hero_power = AsHeroPower(opponent.Board.FirstOrDefault(e => e.IsHeroPower)),
                        weapon = AsWeapon(opponent.Board.FirstOrDefault(e => e.IsWeapon)),
                        board = BoardCards(opponent.Board),
                        secrets_count = opponent.Secrets.Count(),
                        played_this_turn = PublicPlays(opponent.CardsPlayedThisTurn),
                    },
                };
            }

            if (!inGame && _lastPhaseSignature != "menu")
            {
                _lastPhaseSignature = "menu";
                TryDiagnostic("Menu lub gra nieaktywna");
            }
            // Atomic replace makes it impossible for MCP to read partial JSON.
            string target = StatePath;
            string folder = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            string temp = target + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(output, Formatting.None), new UTF8Encoding(false));
            if (File.Exists(target))
                File.Replace(temp, target, null);
            else
                File.Move(temp, target);
        }
    }
}
