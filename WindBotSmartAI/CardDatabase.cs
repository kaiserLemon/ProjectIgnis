// CardDatabase.cs
// Drop into: WindBot/Game/AI/CardDatabase.cs
//
// Reads all cards.cdb files from the EDOPro expansions folder and caches
// card metadata so the SmartExecutor can make heuristic decisions without
// hard-coding individual card IDs.
//
// Uses Mono.Data.Sqlite (already shipped with WindBot).

using System;
using System.Collections.Generic;
using System.IO;
using Mono.Data.Sqlite;

namespace WindBot.Game.AI
{
    // -----------------------------------------------------------------------
    // Card type bitmasks  (datas.type column)
    // Verified against ProjectIgnis/expansions/cards.cdb
    // -----------------------------------------------------------------------
    public static class CardTypeMask
    {
        public const int Monster     = 0x000001;
        public const int Spell       = 0x000002;
        public const int Trap        = 0x000004;
        public const int Normal      = 0x000010;  // Normal Monster / Normal Spell / Normal Trap
        public const int Effect      = 0x000020;
        public const int Fusion      = 0x000040;
        public const int Ritual      = 0x000080;
        public const int TrapMonster = 0x000100;
        public const int Spirit      = 0x000200;
        public const int Union       = 0x000400;
        public const int Gemini      = 0x000800;
        public const int Tuner       = 0x001000;
        public const int Synchro     = 0x002000;
        public const int Token       = 0x004000;
        public const int QuickPlay   = 0x010000;  // Quick-Play Spell (bit also used for quick effects on monsters? no — see notes)
        public const int Continuous  = 0x020000;  // Continuous Spell or Continuous Trap
        public const int Equip       = 0x040000;
        public const int Field       = 0x080000;
        public const int Counter     = 0x100000;  // Counter Trap
        public const int Flip        = 0x200000;
        public const int Toon        = 0x400000;
        public const int Xyz         = 0x800000;
        public const int Pendulum    = 0x1000000;
        public const int SpSummonOnly= 0x2000000;  // Cannot be Normal Summoned/Set
        public const int Link        = 0x4000000;
    }

    // -----------------------------------------------------------------------
    // Card category bitmasks  (datas.category column)
    // What the card's effect DOES, not what type it is.
    // Verified against ProjectIgnis/expansions/cards.cdb
    // -----------------------------------------------------------------------
    public static class CardCategoryMask
    {
        public const int Destroy        = 0x000001;  // destroys monsters
        public const int DestroyST      = 0x000002;  // destroys spells/traps (Heavy Storm, MST)
        public const int SendToGY       = 0x000004;  // sends to graveyard (Foolish Burial)
        public const int ToHand         = 0x000008;  // returns cards to hand
        public const int Search         = 0x000020;  // add from deck to hand (Terraforming, RotA)
        public const int Banish         = 0x000080;  // banishes cards
        public const int Draw           = 0x000100;  // draw cards (One Day of Peace, Maxx C)
        public const int AddToHand      = 0x000200;  // add specific card to hand
        public const int ChangePosition = 0x001000;  // changes monster position (Book of Moon)
        public const int TakeControl    = 0x010000;  // takes control of opponent's monster
        public const int Negate         = 0x020000;  // negates effects (Ash, Veiler, Impermanence)
        public const int Disable        = 0x040000;  // disables effects without negating (Skill Drain style)
        public const int SpecialSummon  = 0x100000;  // special summons a monster
    }

    // -----------------------------------------------------------------------
    // Metadata for a single card
    // -----------------------------------------------------------------------
    public class CardMeta
    {
        public int    Id;
        public string Name;
        public string Desc;
        public int    Type;       // CardTypeMask flags
        public int    Atk;        // Base ATK (-2 = variable)
        public int    Def;        // Base DEF (-2 = variable)
        public int    Level;      // Level / Rank / Link Rating
        public int    Race;       // Monster race (sub-type)
        public int    Attribute;  // Monster attribute
        public int    Category;   // CardCategoryMask flags

        // ------------------------------------------------------------------
        // Type helpers
        // ------------------------------------------------------------------
        public bool IsMonster()         => (Type & CardTypeMask.Monster)    != 0;
        public bool IsSpell()           => (Type & CardTypeMask.Spell)      != 0;
        public bool IsTrap()            => (Type & CardTypeMask.Trap)       != 0;
        public bool IsNormalSpell()     => (Type & CardTypeMask.Spell)  != 0 && (Type & CardTypeMask.QuickPlay) == 0
                                          && (Type & CardTypeMask.Continuous) == 0 && (Type & CardTypeMask.Equip) == 0
                                          && (Type & CardTypeMask.Field) == 0 && (Type & CardTypeMask.Ritual) == 0;
        public bool IsQuickPlaySpell()  => (Type & CardTypeMask.Spell)      != 0 && (Type & CardTypeMask.QuickPlay) != 0;
        public bool IsContinuousSpell() => (Type & CardTypeMask.Spell)      != 0 && (Type & CardTypeMask.Continuous) != 0;
        public bool IsFieldSpell()      => (Type & CardTypeMask.Field)      != 0;
        public bool IsEquipSpell()      => (Type & CardTypeMask.Equip)      != 0;
        public bool IsRitualSpell()     => (Type & CardTypeMask.Spell)      != 0 && (Type & CardTypeMask.Ritual) != 0;
        public bool IsNormalTrap()      => (Type & CardTypeMask.Trap)       != 0
                                          && (Type & CardTypeMask.Continuous) == 0 && (Type & CardTypeMask.Counter) == 0;
        public bool IsContinuousTrap()  => (Type & CardTypeMask.Trap)       != 0 && (Type & CardTypeMask.Continuous) != 0;
        public bool IsCounterTrap()     => (Type & CardTypeMask.Trap)       != 0 && (Type & CardTypeMask.Counter) != 0;
        public bool IsNormalMonster()   => (Type & CardTypeMask.Normal)     != 0 && (Type & CardTypeMask.Monster) != 0;
        public bool IsEffectMonster()   => (Type & CardTypeMask.Effect)     != 0;
        public bool IsTuner()           => (Type & CardTypeMask.Tuner)      != 0;
        public bool IsFusionMonster()   => (Type & CardTypeMask.Fusion)     != 0;
        public bool IsSynchroMonster()  => (Type & CardTypeMask.Synchro)    != 0;
        public bool IsXyzMonster()      => (Type & CardTypeMask.Xyz)        != 0;
        public bool IsLinkMonster()     => (Type & CardTypeMask.Link)       != 0;
        public bool IsPendulumMonster() => (Type & CardTypeMask.Pendulum)   != 0;
        public bool IsExtraMonster()    => IsFusionMonster() || IsSynchroMonster() || IsXyzMonster() || IsLinkMonster();

        // ------------------------------------------------------------------
        // Category / effect helpers
        // ------------------------------------------------------------------
        public bool Searches()        => (Category & (CardCategoryMask.Search | CardCategoryMask.AddToHand)) != 0;
        public bool Draws()           => (Category & CardCategoryMask.Draw) != 0;
        public bool Negates()         => (Category & CardCategoryMask.Negate) != 0;
        public bool Destroys()        => (Category & (CardCategoryMask.Destroy | CardCategoryMask.DestroyST)) != 0;
        public bool DestroyMonsters() => (Category & CardCategoryMask.Destroy)   != 0;
        public bool DestroysBackrow() => (Category & CardCategoryMask.DestroyST) != 0;
        public bool Banishes()        => (Category & CardCategoryMask.Banish) != 0;
        public bool SpecialSummons()  => (Category & CardCategoryMask.SpecialSummon) != 0;
        public bool ChangesPosition() => (Category & CardCategoryMask.ChangePosition) != 0;
        public bool TakesControl()    => (Category & CardCategoryMask.TakeControl) != 0;

        // ------------------------------------------------------------------
        // Text-based fallback heuristics (for cards with sparse category data)
        // ------------------------------------------------------------------
        private bool DescContains(string kw) =>
            Desc != null && Desc.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0;

        // A card is likely a hand-trap if it can be activated from the hand
        // on the opponent's turn. We detect this via text patterns since the
        // type field doesn't encode it for monsters.
        public bool LikelyHandTrap() =>
            IsMonster() && (
                DescContains("discard this card") ||
                DescContains("during your opponent's") ||
                DescContains("(Quick Effect)")
            );

        // Whether this card tends to be more valuable on the opponent's turn
        public bool PreferOpponentTurn() =>
            IsCounterTrap() ||
            IsQuickPlaySpell() ||
            Negates() ||
            LikelyHandTrap() ||
            (IsNormalTrap() && (Negates() || DescContains("when") || DescContains("if your opponent")));

        // Whether this card should be activated as early as possible (MP1)
        public bool PreferEarlyActivation() =>
            IsFieldSpell() ||
            IsContinuousSpell() ||
            Searches() ||
            Draws();

        // Rough "danger level" of the card to the opponent. Used to decide
        // whether the opponent's removal / negation is worth chaining against.
        public int ThreatLevel()
        {
            int score = 0;
            if (Negates())        score += 3;
            if (DestroyMonsters())score += 2;
            if (DestroysBackrow())score += 2;
            if (TakesControl())   score += 4;
            if (Banishes())       score += 2;
            if (Searches())       score += 2;
            if (Draws())          score += 1;
            if (SpecialSummons()) score += 1;
            if (IsCounterTrap())  score += 1; // counter traps are unresponsive
            return score;
        }
    }

    // -----------------------------------------------------------------------
    // Singleton cache — call Load() once at bot startup
    // -----------------------------------------------------------------------
    public static class CardDatabase
    {
        private static readonly Dictionary<int, CardMeta> _cache = new Dictionary<int, CardMeta>();
        private static bool _loaded = false;
        private static readonly object _lock = new object();

        /// <summary>
        /// Load one or more .cdb files.  Call this once before the first duel.
        /// Duplicate IDs are kept from the first file that defines them (main
        /// cards.cdb takes precedence over expansion patches).
        /// </summary>
        public static void Load(IEnumerable<string> dbPaths)
        {
            lock (_lock)
            {
                if (_loaded) return;
                foreach (var path in dbPaths)
                {
                    if (!File.Exists(path)) continue;
                    try   { LoadSingle(path); }
                    catch { /* skip broken / inaccessible databases */ }
                }
                _loaded = true;
            }
        }

        /// <summary>Convenience overload for a single path.</summary>
        public static void Load(string singlePath) => Load(new[] { singlePath });

        /// <summary>Auto-discover all .cdb files in a directory.</summary>
        public static void LoadDirectory(string dir, string searchPattern = "*.cdb")
        {
            if (!Directory.Exists(dir)) return;
            // Load main cards.cdb first so it takes precedence
            var files = new List<string>(Directory.GetFiles(dir, searchPattern));
            files.Sort((a, b) =>
            {
                bool aMain = Path.GetFileName(a).Equals("cards.cdb", StringComparison.OrdinalIgnoreCase);
                bool bMain = Path.GetFileName(b).Equals("cards.cdb", StringComparison.OrdinalIgnoreCase);
                if (aMain && !bMain) return -1;
                if (!aMain && bMain) return  1;
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });
            Load(files);
        }

        private static void LoadSingle(string path)
        {
            string connStr = "Data Source=" + path + ";Version=3;Read Only=True;";
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT d.id, t.name, t.desc,
                               d.type, d.atk, d.def, d.level,
                               d.race, d.attribute, d.category
                        FROM datas d
                        JOIN  texts t ON d.id = t.id";

                    using (var rdr = cmd.ExecuteReader())
                    {
                        while (rdr.Read())
                        {
                            int id = rdr.GetInt32(0);
                            if (_cache.ContainsKey(id)) continue; // don't overwrite with patch data

                            _cache[id] = new CardMeta
                            {
                                Id        = id,
                                Name      = rdr.IsDBNull(1) ? "" : rdr.GetString(1),
                                Desc      = rdr.IsDBNull(2) ? "" : rdr.GetString(2),
                                Type      = rdr.GetInt32(3),
                                Atk       = rdr.GetInt32(4),
                                Def       = rdr.GetInt32(5),
                                Level     = rdr.GetInt32(6),
                                Race      = rdr.GetInt32(7),
                                Attribute = rdr.GetInt32(8),
                                Category  = rdr.GetInt32(9),
                            };
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Look up a card's metadata by ID.  Returns null if not found or
        /// if the database was never loaded.
        /// </summary>
        public static CardMeta Get(int id)
        {
            _cache.TryGetValue(id, out var meta);
            return meta;
        }

        public static bool IsLoaded => _loaded;
        public static int  Count    => _cache.Count;
    }
}
