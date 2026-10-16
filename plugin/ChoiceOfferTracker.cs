using System;
using System.Collections.Generic;
using System.Linq;

namespace HearthstoneMcpPlugin
{
    /// <summary>
    /// HDT clears OfferedEntityIds on CHOICES_CHOSEN. When that log event is
    /// dropped, the list can remain populated after the in-game popup closes.
    /// Recognize resolution from public changes instead; never use a timer
    /// alone to dismiss an otherwise unchanged, legitimate Discover screen.
    /// This class is also compiled by Coach.Checks for regression testing.
    /// </summary>
    public sealed class ChoiceOfferTracker
    {
        private int[] _offerIds = Array.Empty<int>();
        private int[] _handIds = Array.Empty<int>();
        private int[] _boardIds = Array.Empty<int>();
        private int[] _playedIds = Array.Empty<int>();
        private int[] _offeredInHandOrPlay = Array.Empty<int>();
        private int _turn;
        private DateTime _startedUtc;
        private bool _suppressed;
        private string _suppressedReason = "";

        public void Reset()
        {
            _offerIds = Array.Empty<int>();
            _handIds = Array.Empty<int>();
            _boardIds = Array.Empty<int>();
            _playedIds = Array.Empty<int>();
            _offeredInHandOrPlay = Array.Empty<int>();
            _turn = -1;
            _startedUtc = DateTime.MinValue;
            _suppressed = false;
            _suppressedReason = "";
        }

        public bool Evaluate(
            IEnumerable<int> offeredIds, IEnumerable<int> handEntityIds,
            IEnumerable<int> boardEntityIds, IEnumerable<int> playedEntityIds,
            IEnumerable<int> alreadyInHandOrPlay, bool allOffersResolved,
            int turn, DateTime nowUtc, out string reason)
        {
            int[] offers = Normalize(offeredIds);
            if (offers.Length == 0)
            {
                Reset();
                reason = "none";
                return false;
            }

            int[] hand = Normalize(handEntityIds);
            int[] board = Normalize(boardEntityIds);
            int[] played = Normalize(playedEntityIds);
            int[] offerOnBoardOrHand = Normalize(alreadyInHandOrPlay)
                .Where(id => Array.BinarySearch(offers, id) >= 0).ToArray();

            if (!_offerIds.SequenceEqual(offers))
            {
                _offerIds = offers;
                _handIds = hand;
                _boardIds = board;
                _playedIds = played;
                _offeredInHandOrPlay = offerOnBoardOrHand;
                _turn = turn;
                _startedUtc = nowUtc;
                _suppressed = false;
                _suppressedReason = "";
            }

            if (_suppressed)
            {
                reason = _suppressedReason;
                return false;
            }

            // Never invent an active popup from unresolved entity IDs. This is
            // normally a transient HDT snapshot as log events are processed.
            if (!allOffersResolved)
            {
                reason = "missing_offer_entities";
                return false;
            }

            // A choice may legitimately OFFER cards from hand, so compare the
            // current zones with the first observed zones rather than assuming
            // that any offered card in hand means an already completed choice.
            if (!_offeredInHandOrPlay.SequenceEqual(offerOnBoardOrHand))
                return Suppress("selected_offer_moved", out reason);

            if (turn != _turn)
                return Suppress("offer_from_previous_turn", out reason);

            // Allow one second for a just-played card's effects to settle as
            // the initial popup appears; after that, continuing to play cards,
            // summoning to the board or changing the hand means the choice is
            // no longer blocking the player. Do NOT expire on elapsed time alone.
            if (nowUtc - _startedUtc >= TimeSpan.FromSeconds(1))
            {
                if (!_playedIds.SequenceEqual(played))
                    return Suppress("play_after_offer", out reason);
                if (!_handIds.SequenceEqual(hand))
                    return Suppress("hand_changed_after_offer", out reason);
                if (!_boardIds.SequenceEqual(board))
                    return Suppress("board_changed_after_offer", out reason);
            }

            reason = "active_offer";
            return true;
        }

        private bool Suppress(string message, out string reason)
        {
            _suppressed = true;
            _suppressedReason = message;
            reason = message;
            return false;
        }

        private static int[] Normalize(IEnumerable<int> values) =>
            values.Where(x => x > 0).Distinct().OrderBy(x => x).ToArray();
    }
}
