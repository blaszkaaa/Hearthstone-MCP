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

        public string Name => "Hearthstone MCP";
        public string Description => "Udostepnia stan meczu i aktywne wybory kart (Odkryj / Choose One) do MCP.";
        public string ButtonText => "Pokaz plik danych";
        public string Author => "Hearthstone-MCP contributors";
        public Version Version => new Version(0, 2, 0);
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
            OnUpdate();
        }

        public void OnUnload()
        {
            _enabled = false;
            try { WriteSnapshot(false); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
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
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Hearthstone MCP: " + ex); }
        }

        private static object AsCard(Entity entity, bool withCombatData)
        {
            if (entity == null)
                return null;

            // Do not access opponent's hidden card identities. This helper is only
            // called for own hand and public board / played cards.
            if (entity.Info.Hidden || !entity.HasCardId)
                return new { entity_id = entity.Id, known = false, position = entity.ZonePosition };

            return new
            {
                entity_id = entity.Id,
                known = true,
                card_id = entity.CardId,
                name = entity.LocalizedName ?? entity.Name ?? entity.CardId,
                card_text = entity.Card.Text,
                card_text_en = entity.Card.EnglishText,
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

        private static void WriteSnapshot(bool allowGame)
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
                        ? AsCard(offered, false)
                        : new { entity_id = id, known = false };
                    return new
                    {
                        index = index + 1,
                        card = card,
                    };
                }).ToArray();

                output = new
                {
                    schema_version = 1,
                    captured_at_utc = DateTime.UtcNow.ToString("o"),
                    source = "hearthstone-deck-tracker-plugin",
                    in_game = true,
                    turn = game.GetTurnNumber(),
                    my_turn = playerEntity != null && playerEntity.GetTag(GameTag.CURRENT_PLAYER) == 1,
                    // Choice types include Discover, Choose One and other
                    // decision prompts. This is not necessarily Discover.
                    choice_tracking_supported = true,
                    choice_active = offeredIds.Length > 0,
                    choice_count = offeredIds.Length,
                    choices = choices,
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
