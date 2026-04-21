# WindBot Smart AI — Installation Guide

Two files to drop into the WindBot source + two small edits.  
Total time: ~5 minutes once you have the repo.

---

## Step 1 — Clone the WindBot source

```bat
cd C:\ProjectIgnis
git clone https://github.com/ProjectIgnis/WindBot.git WindBotSrc
cd WindBotSrc
```

---

## Step 2 — Copy the new files

```bat
copy C:\ProjectIgnis\WindBotSmartAI\CardDatabase.cs   WindBotSrc\Game\AI\CardDatabase.cs
copy C:\ProjectIgnis\WindBotSmartAI\SmartExecutor.cs  WindBotSrc\Game\AI\Decks\SmartExecutor.cs
```

---

## Step 3 — Register SmartExecutor as the default fallback

Open `WindBotSrc\Game\AI\DecksManager.cs` and find the method that returns an
executor when no matching deck name is found (it's usually called something like
`GetExecutor` or has a `default:` / `else` branch).

Change the fallback from `DefaultNoExecutor` to `SmartExecutor`:

```csharp
// BEFORE
return new DefaultNoExecutor(ai, duel);

// AFTER
return new SmartExecutor(ai, duel);
```

If you also want the "Feelin' Lucky" bot entry in bots.json to use SmartExecutor,
find the line that maps `"Lucky"` and change its executor class the same way.

---

## Step 4 — Load the card database at startup

Open `WindBotSrc\Program.cs` (or wherever the main entry point is) and add one
call before the first duel starts:

```csharp
// At the top of the file:
using WindBot.Game.AI;

// In Main(), before starting any bot:
CardDatabase.LoadDirectory(
    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, 
                 @"..\..\expansions")   // adjust to where your .cdb files live
);

// Or point directly at cards.cdb:
// CardDatabase.Load(@"C:\ProjectIgnis\expansions\cards.cdb");
```

The database loads once and is cached for the whole session — no per-turn cost.

---

## Step 5 — Add the Mono.Data.Sqlite reference

`CardDatabase.cs` uses `Mono.Data.Sqlite`.  The DLL already ships with WindBot,
so just make sure it's referenced in the project:

In Visual Studio: Right-click the project → Add Reference → Browse →
select `C:\ProjectIgnis\WindBot\Mono.Data.Sqlite.dll`.

Or in the `.csproj` file:

```xml
<Reference Include="Mono.Data.Sqlite">
  <HintPath>..\WindBot\Mono.Data.Sqlite.dll</HintPath>
</Reference>
```

---

## Step 6 — Build and deploy

```bat
cd WindBotSrc
dotnet build -c Release
```

Then copy the output DLLs back to your live WindBot folder:

```bat
copy bin\Release\net*\ExecutorBase.dll   C:\ProjectIgnis\WindBot\ExecutorBase.dll
copy bin\Release\net*\WindBot.exe        C:\ProjectIgnis\WindBot\WindBot.exe
```

(Exact output path depends on your .NET target framework.)

---

## What changed vs "Feelin' Lucky"

| Situation | Before (Feelin' Lucky) | After (SmartExecutor) |
|-----------|------------------------|------------------------|
| Battle phase | Attacks randomly | Only attacks when trade is profitable; goes to MP2 to set traps otherwise |
| Attack target | Random | Highest-ATK target our monster can beat |
| Monster position | Random | ATK if we outmatch best enemy; DEF if we don't |
| Trap activation | Activates immediately on our turn | Always sets first; activates on opponent's turn |
| Quick-play spells | Activates randomly | Held for opponent's turn unless it searches |
| Field/Continuous spells | Might skip | Activates immediately in MP1 |
| Searchers | No priority | Highest priority in MP1 |
| Hand traps | Random | Chained to opponent's effects on their turn |
| OTK check | Never calculated | Attacks everything when lethal is reachable |

---

## Tuning

At the top of `SmartExecutor.cs` are four constants you can adjust:

```csharp
private const int LpDefensiveThreshold  = 2000;  // switch to defense mode below this LP
private const int FaceDownAttackLpMargin = 1000; // only attack face-downs if ahead by this much
private const int SafeTradeBuffer        = 0;    // require at least this ATK advantage to trade
private const int MaxAcceptableLpLoss    = 1500; // don't take unfavorable battles (reserved)
```

Raise `SafeTradeBuffer` to make the bot more conservative; lower it to be more
aggressive. Set `LpDefensiveThreshold` to 0 to never switch to defense mode.
