// SmartExecutor.cs
// Drop into: WindBot/Game/AI/Decks/SmartExecutor.cs
//
// A generalizable executor that works for ANY deck without needing
// hard-coded card-ID logic.  It improves on DefaultNoExecutor on two levels:
//
//   PATH 1 — Pure game logic
//     • Battle phase: LP math, favorable trades, avoiding losing battles
//     • Monster position: ATK vs best enemy monster, LP deficit -> defense
//     • Spell ordering: field spells first, then searchers, then reactive cards
//     • Trap timing: always set first, never activate on your own turn unless
//       the trap has immediate value (e.g. Scapegoat with no cost)
//
//   PATH 2 — Card metadata heuristics (via CardDatabase)
//     • Negation cards (Ash, Imperm, Counter Traps) held for opponent's turn
//     • Searchers / draw cards activated in MP1 before anything else
//     • Destruction spells used when opponent has targets worth removing
//     • Quick-play spells preferentially saved for opponent's turn
//     • Opponent's high-threat activations trigger our negate chain

using System;
using System.Collections.Generic;
using System.Linq;
using WindBot.Game;
using WindBot.Game.AI;

namespace WindBot.Game.AI.Decks
{
    [Deck("Smart", "AI_Smart")]  // fallback deck — see DecksManager registration note
    public class SmartExecutor : DefaultExecutor
    {
        // ----------------------------------------------------------------
        // Tunables — adjust to change aggression / risk tolerance
        // ----------------------------------------------------------------

        // If bot's LP drops below this, prefer setting monsters in defense
        private const int LpDefensiveThreshold = 2000;

        // Attack a face-down monster only if we're this many LP ahead
        private const int FaceDownAttackLpMargin = 1000;

        // Minimum ATK advantage over an enemy monster to consider the trade safe
        private const int SafeTradeBuffer = 0;

        // We'll hard-pass on attacking if our monster loses > this many LP
        // (unless it ends the game)
        private const int MaxAcceptableLpLoss = 1500;

        // ----------------------------------------------------------------
        // Constructor
        // ----------------------------------------------------------------
        public SmartExecutor(GameAI ai, Duel duel) : base(ai, duel)
        {
            // The base DefaultExecutor constructor already registers handlers
            // for Ash Blossom, Called by the Grave, Raigeki, Dark Hole, etc.
            // We layer smarter generic handlers on top via AddExecutor.

            // --- Spell activation priority (MP1, our turn) ---------------
            // Field spells: activate immediately for sustained advantage
            AddExecutor(ExecutorType.Activate, ActivateFieldSpell);
            // Searchers / draw spells: activate early to extend hand
            AddExecutor(ExecutorType.Activate, ActivateSearcherOrDraw);
            // Generic safe activations (destroy, banish, etc.)
            AddExecutor(ExecutorType.Activate, ActivateGenericRemoval);
            // Continuous spells: activate once board is stable
            AddExecutor(ExecutorType.Activate, ActivateContinuousSpell);
            // Quick-play spells: prefer to hold for opponent's turn
            AddExecutor(ExecutorType.Activate, ActivateQuickPlayOnOurTurn);

            // --- Trap / hand-trap decisions ------------------------------
            // Always SET traps on our turn instead of activating immediately
            AddExecutor(ExecutorType.Set, SetTrapSmart);
            // Chain trap activations to opponent's threats
            AddExecutor(ExecutorType.Activate, ActivateTrapOnOpponentTurn);

            // --- Monster actions ----------------------------------------
            // Reposition monsters based on board state
            AddExecutor(ExecutorType.Repos, SmartReposition);
            // Normal summon: pick the best option
            AddExecutor(ExecutorType.Summon, SmartNormalSummon);
        }

        // ================================================================
        //  POSITION SELECTION (Path 1 + 2)
        //  Called when the game asks "attack or defense position?"
        // ================================================================
        public override CardPosition OnSelectPosition(int cardId, IList<CardPosition> positions)
        {
            // Default: prefer attack position
            CardPosition preferred = CardPosition.FaceUpAttack;

            // If we're low on LP, go defensive
            if (Bot.LifePoints <= LpDefensiveThreshold && positions.Contains(CardPosition.FaceUpDefence))
                preferred = CardPosition.FaceUpDefence;

            // Compare base stats vs best enemy monster
            var meta = CardDatabase.Get(cardId);
            if (meta != null && meta.IsMonster())
            {
                int myAtk = meta.Atk < 0 ? 0 : meta.Atk;
                int myDef = meta.Def < 0 ? 0 : meta.Def;
                int bestEnemyAtk = GetBestEnemyAtkValue();

                bool canWinAtk = myAtk > bestEnemyAtk || Enemy.GetMonsterCount() == 0;
                bool defIsHigher = myDef > myAtk;

                if (!canWinAtk && defIsHigher && positions.Contains(CardPosition.FaceUpDefence))
                    preferred = CardPosition.FaceUpDefence;

                // Low-ATK utility monsters should hide in defense
                if (myAtk <= 500 && myDef > 500 && positions.Contains(CardPosition.FaceUpDefence))
                    preferred = CardPosition.FaceUpDefence;
            }

            return positions.Contains(preferred) ? preferred : positions[0];
        }

        // ================================================================
        //  BATTLE PHASE — choose action (Path 1)
        //  Called each step of the battle phase.
        // ================================================================
        public override BattlePhaseAction OnSelectBattleCmd(bool canMainPhaseTwo, bool canEndPhase)
        {
            // 1. Can we end the duel this turn?  If yes, attack everything.
            if (CanOtkThisTurn())
                return BattlePhaseAction.Attack;

            // 2. Do we have at least one favorable attack?
            if (HasFavorableAttack())
                return BattlePhaseAction.Attack;

            // 3. All our monsters are outmatched.
            //    Go to MP2 if we have cards to set (traps, quick-plays).
            if (canMainPhaseTwo && HasCardsWorthSettingInMP2())
                return BattlePhaseAction.MainPhaseTwo;

            // 4. Nothing useful to do — end the turn.
            return canEndPhase ? BattlePhaseAction.End : BattlePhaseAction.Attack;
        }

        // ================================================================
        //  ATTACKER SELECTION (Path 1)
        //  Returns which of our monsters should attack this step.
        // ================================================================
        public override ClientCard OnSelectAttacker(IList<ClientCard> attackers, IList<ClientCard> defenders)
        {
            if (attackers == null || attackers.Count == 0) return null;

            // If we can OTK, attack with highest ATK monster first
            if (CanOtkThisTurn())
                return attackers.OrderByDescending(c => c.Attack).FirstOrDefault();

            // Find the best trade: our monster beats an enemy monster and the
            // leftover ATK is maximized (we gain the most advantage)
            ClientCard bestAttacker = null;
            int bestScore = int.MinValue;

            foreach (var attacker in attackers)
            {
                // Can this monster profitably attack any face-up monster?
                foreach (var defender in defenders.Where(d => d.IsFaceup()))
                {
                    int defValue = defender.IsAttack() ? defender.Attack : defender.Defense;
                    int margin = attacker.Attack - defValue;
                    if (margin >= SafeTradeBuffer && margin > bestScore)
                    {
                        bestScore = margin;
                        bestAttacker = attacker;
                    }
                }

                // Or direct attack / face-down?
                if (defenders.Count == 0 || defenders.All(d => !d.IsFaceup()))
                {
                    if (attacker.Attack > bestScore)
                    {
                        bestScore = attacker.Attack;
                        bestAttacker = attacker;
                    }
                }
            }

            // Fallback: the attacker with the highest ATK
            return bestAttacker ?? attackers.OrderByDescending(c => c.Attack).FirstOrDefault();
        }

        // ================================================================
        //  ATTACK TARGET SELECTION (Path 1)
        //  Given our attacker, pick the best thing to attack.
        // ================================================================
        public override ClientCard OnSelectAttackTarget(ClientCard attacker, IList<ClientCard> defenders)
        {
            if (defenders == null || defenders.Count == 0) return null;

            int myAtk = attacker.Attack;

            // Collect targets we can destroy without dying
            var winnableTargets = defenders
                .Where(d => d.IsFaceup() && myAtk > (d.IsAttack() ? d.Attack : d.Defense))
                .ToList();

            if (winnableTargets.Any())
            {
                // Attack the highest-ATK winnable target (best trade / most LP damage)
                return winnableTargets.OrderByDescending(d => d.IsAttack() ? d.Attack : d.Defense).First();
            }

            // No winnable face-up target.  Consider face-downs if we're ahead on LP.
            var faceDownTargets = defenders.Where(d => !d.IsFaceup()).ToList();
            if (faceDownTargets.Any() && Bot.LifePoints - Enemy.LifePoints > FaceDownAttackLpMargin)
                return faceDownTargets.First();

            // Consider an unfavorable attack only if it ends the game
            if (CanOtkThisTurn())
                return defenders.OrderBy(d => d.IsAttack() ? d.Attack : d.Defense).First();

            // Nothing good — cancel the attack (return null to not attack)
            return null;
        }

        // ================================================================
        //  CHAIN DECISIONS (Path 1 + 2)
        //  Called when the game asks if we want to chain a card.
        // ================================================================
        public override bool OnSelectChain(bool cancelable)
        {
            // Card is the card we're being asked to chain.
            if (Card == null) return false;

            var meta = CardDatabase.Get(Card.Id);

            // On our own turn: only chain if it adds immediate value
            if (Duel.Player == 0)
            {
                // Searchers and draw cards: always beneficial to chain
                if (meta != null && (meta.Searches() || meta.Draws())) return true;
                // Otherwise hold reactive cards for opponent's turn
                if (meta != null && meta.PreferOpponentTurn()) return false;
                return true;
            }

            // On opponent's turn: chain if this card is reactive / negation
            // and there is something worth chaining against
            if (meta != null)
            {
                // Always chain negation cards
                if (meta.Negates()) return true;
                // Chain removal when opponent has a target we want gone
                if (meta.Destroys() && Enemy.GetMonsterCount() > 0) return true;
                // Chain hand traps freely
                if (meta.LikelyHandTrap()) return true;
            }

            // Default: chain if the card can be chained (non-cancelable means
            // the game is forcing us, so we go ahead)
            return !cancelable;
        }

        // ================================================================
        //  EFFECT YES/NO (Path 2)
        //  Should we activate an optional effect?
        // ================================================================
        public override bool OnSelectEffectYn(ClientCard card, int desc)
        {
            var meta = CardDatabase.Get(card.Id);
            if (meta == null) return true; // unknown card: default yes

            // Refuse effects that damage us when we're already low
            if (meta.Burns() && Bot.LifePoints < 2000) return false;

            // Refuse cost effects that discard when hand is tiny
            if (Bot.GetHandCount() <= 1) return false; // be conservative

            return true;
        }

        // ================================================================
        //  SPELL ACTIVATION HANDLERS (Path 1 + 2)
        // ================================================================

        /// Activate field spells immediately — they provide ongoing value.
        private bool ActivateFieldSpell()
        {
            var meta = CardDatabase.Get(Card.Id);
            return meta != null && meta.IsFieldSpell();
        }

        /// Activate searchers and draw effects early in MP1.
        private bool ActivateSearcherOrDraw()
        {
            if (Duel.Player != 0) return false; // our turn only
            var meta = CardDatabase.Get(Card.Id);
            if (meta == null) return false;
            return meta.PreferEarlyActivation() && !meta.IsTrap() && !meta.IsQuickPlaySpell();
        }

        /// Activate removal / destruction spells when the opponent has good targets.
        private bool ActivateGenericRemoval()
        {
            if (Duel.Player != 0) return false;
            var meta = CardDatabase.Get(Card.Id);
            if (meta == null) return false;
            if (!meta.IsSpell()) return false;

            bool opponentHasMonsters = Enemy.GetMonsterCount() > 0;
            bool opponentHasBackrow  = Enemy.GetSpellCount()   > 0;

            if (meta.DestroyMonsters() && opponentHasMonsters) return true;
            if (meta.DestroysBackrow() && opponentHasBackrow)  return true;
            if (meta.Banishes()        && opponentHasMonsters)  return true;

            return false;
        }

        /// Activate continuous spells once we've done our searching.
        private bool ActivateContinuousSpell()
        {
            if (Duel.Player != 0) return false;
            var meta = CardDatabase.Get(Card.Id);
            return meta != null && meta.IsContinuousSpell();
        }

        /// Quick-play spells: hold them for the opponent's turn unless we're
        /// under direct attack pressure right now.
        private bool ActivateQuickPlayOnOurTurn()
        {
            var meta = CardDatabase.Get(Card.Id);
            if (meta == null || !meta.IsQuickPlaySpell()) return false;

            // On our own turn: only activate if it's a searcher/draw or if we're
            // in desperate need (very low LP).
            if (Duel.Player == 0)
                return meta.Searches() || meta.Draws() || Bot.LifePoints < LpDefensiveThreshold;

            // Opponent's turn: activate quick-plays freely
            return true;
        }

        // ================================================================
        //  TRAP HANDLERS (Path 1 + 2)
        // ================================================================

        /// On our turn: always SET traps rather than activating them.
        /// Exceptions: continuous traps that help us immediately,
        /// or traps the game is forcing us to activate (non-cancelable).
        private bool SetTrapSmart()
        {
            if (Duel.Player != 0) return false; // only relevant on our turn
            var meta = CardDatabase.Get(Card.Id);
            if (meta == null || !meta.IsTrap()) return false;

            // Set any trap that prefers the opponent's turn
            if (meta.IsNormalTrap() || meta.IsCounterTrap()) return true;

            // Continuous traps: set unless they help right now
            if (meta.IsContinuousTrap() && !meta.Negates()) return true;

            return false;
        }

        /// On opponent's turn: activate traps that are useful.
        private bool ActivateTrapOnOpponentTurn()
        {
            if (Duel.Player == 0) return false; // only on opponent's turn
            var meta = CardDatabase.Get(Card.Id);
            if (meta == null || !meta.IsTrap()) return false;

            // Negate traps: chain to the opponent's activations
            if (meta.Negates()) return true;

            // Destruction traps: use when opponent has good targets
            if (meta.Destroys() && Enemy.GetMonsterCount() > 0) return true;

            // Continuous traps that disable / restrict opponent
            if (meta.IsContinuousTrap()) return true;

            // Default: let the base executor decide
            return false;
        }

        // ================================================================
        //  MONSTER REPOSITIONING (Path 1)
        //  Called when an already-summoned monster can change position.
        // ================================================================
        private bool SmartReposition()
        {
            if (Card == null) return false;
            int bestEnemyAtk = GetBestEnemyAtkValue();

            // Switch to attack if we now outmatch the best enemy monster
            if (Card.IsDefense() && Card.Attack > bestEnemyAtk && Enemy.GetMonsterCount() > 0)
                return true;

            // Switch to defense if we can no longer safely attack
            if (Card.IsAttack() && Card.Attack < bestEnemyAtk - SafeTradeBuffer
                && Card.Defense > Card.Attack)
                return true;

            // Switch to defense if we're in danger
            if (Card.IsAttack() && Bot.LifePoints <= LpDefensiveThreshold
                && Card.Defense > Card.Attack)
                return true;

            return false;
        }

        // ================================================================
        //  NORMAL SUMMON SELECTION (Path 1 + 2)
        // ================================================================
        private bool SmartNormalSummon()
        {
            if (Card == null) return false;
            var meta = CardDatabase.Get(Card.Id);

            // Don't normal summon extra-deck monsters (they need special summon)
            if (meta != null && meta.IsExtraMonster()) return false;

            // If LP is very low, prefer the highest-DEF monster
            if (Bot.LifePoints <= LpDefensiveThreshold)
            {
                // Find the monster in hand with the highest DEF
                var bestDef = Bot.Hand
                    .Where(c => c.HasType(CardType.Monster))
                    .OrderByDescending(c => c.Defense)
                    .FirstOrDefault();
                return bestDef != null && Card.Id == bestDef.Id;
            }

            // Otherwise, summon the highest-ATK available monster
            var bestAtk = Bot.Hand
                .Where(c => c.HasType(CardType.Monster))
                .OrderByDescending(c => c.Attack)
                .FirstOrDefault();
            return bestAtk != null && Card.Id == bestAtk.Id;
        }

        // ================================================================
        //  PRIVATE HELPERS
        // ================================================================

        /// Highest ATK among face-up enemy monsters (0 if none).
        private int GetBestEnemyAtkValue()
        {
            var monsters = Enemy.GetMonsters();
            if (monsters == null || monsters.Count == 0) return 0;
            return monsters.Where(m => m.IsFaceup()).Select(m => m.Attack).DefaultIfEmpty(0).Max();
        }

        /// True if our total ATK exceeds the enemy's LP this turn.
        private bool CanOtkThisTurn()
        {
            var attackers = Bot.GetMonsters()
                .Where(m => m.IsFaceup() && m.IsAttack())
                .ToList();
            int totalAtk = attackers.Sum(m => m.Attack);

            // Simple check: if enemy has no monsters, can we deal lethal?
            if (Enemy.GetMonsterCount() == 0)
                return totalAtk >= Enemy.LifePoints;

            // With enemy monsters: would we clear their field and still deal lethal?
            // (Very rough estimate — proper calculation requires simulation)
            int enemyTotal = Enemy.GetMonsters()
                .Where(m => m.IsFaceup())
                .Sum(m => m.IsAttack() ? m.Attack : m.Defense);
            return totalAtk - enemyTotal >= Enemy.LifePoints;
        }

        /// True if we have at least one attack that trades favorably.
        private bool HasFavorableAttack()
        {
            var myMonsters = Bot.GetMonsters()
                .Where(m => m.IsFaceup() && m.IsAttack())
                .ToList();

            if (!myMonsters.Any()) return false;

            // Direct attack possible
            if (Enemy.GetMonsterCount() == 0) return true;

            // Check favorable trades vs face-up monsters
            var enemyMonsters = Enemy.GetMonsters().Where(m => m.IsFaceup()).ToList();
            foreach (var mine in myMonsters)
            {
                foreach (var theirs in enemyMonsters)
                {
                    int defVal = theirs.IsAttack() ? theirs.Attack : theirs.Defense;
                    if (mine.Attack > defVal + SafeTradeBuffer) return true;
                }

                // Face-down monsters might be worth attacking if we're ahead
                if (enemyMonsters.Count == 0 &&
                    Bot.LifePoints - Enemy.LifePoints > FaceDownAttackLpMargin)
                    return true;
            }
            return false;
        }

        /// True if we have unset traps or quick-plays in hand worth saving for M2.
        private bool HasCardsWorthSettingInMP2()
        {
            return Bot.Hand.Any(c =>
            {
                var meta = CardDatabase.Get(c.Id);
                return meta != null && (meta.IsTrap() || meta.IsQuickPlaySpell());
            });
        }
    }
}
