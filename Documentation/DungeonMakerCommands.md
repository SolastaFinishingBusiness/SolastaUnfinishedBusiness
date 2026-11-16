# Dungeon Maker campaign commands

A guide for campaign authors. With this mod, a Dungeon Maker campaign can read and move the clock, start a dialogue or
a skill check, teleport characters and switch torches by **writing campaign variables with reserved names**, for
example from an Area Activator's Variable Operation.

- You create the variables yourself in the campaign's variable list. Names are case-sensitive. A variable you don't
  create is ignored, so use only the ones you need.
- Players need the mod for any of this to work. Without it the variables are ordinary variables and nothing happens.
- There is no setting to switch this off: the commands work in every campaign while the mod is installed. A campaign
  that doesn't create these variables is not affected.
- The mod writes what it does, and why a command failed, to the mod's log.

Adapted from BlackguardRogue's Dungeon Maker tools.

## Commands

Each command is a **String** variable. Write a name into it; the mod sets it back to empty when the command starts or
ends, so you can write the same name again later.

| Variable | Write | What happens |
|---|---|---|
| `Dialog_Trigger` | the name of an NPC or object | Its dialogue starts, as if a character had clicked it. |
| `Roll_Trigger` | the name of a Roll Activator | The acting character rolls its check. |
| `Teleport_Node` | the name of a Node or another gadget | The acting character is teleported to a free cell next to it. |
| `Teleport_PartyNode` | the name of a Node or another gadget | The whole party, with its guests, is teleported there in formation. |

- **Names.** For `Dialog_Trigger` you can use the gadget's own name (such as `NpcDialog4`), or the name or title you
  gave the NPC when creating it. For the others use the gadget's own name. Capital letters don't matter. If a name
  fits more than one gadget, the command fails and the log lists them. A name as the game translates it for a
  player's language is not looked at, so that every player's game finds the same gadget.
- **Teleport destinations** can be any gadget with its own name, as with the game's own Teleporter. A Node is the
  usual choice, as it shows nothing in the game; a door or a chest works too, so check the name.
- **Finding names.** Create an Int variable `Dialog_ListGadgets` and set it to `1`: the log lists the interactive
  gadgets of the location and whether each has a dialogue or a roll. Set it to `2` to list every gadget with its
  conditions, which is how you find Node and torch names.
- **The dialogue or roll must exist.** Set it up on the NPC, object or Roll Activator as usual. The command only
  starts it another way. The gadget's own conditions still apply: while they are not met, the command waits.
- **One at a time per variable.** Writing a new name while the earlier one is still waiting replaces it. Writing an
  empty text cancels it. The four commands don't hold each other up. `Teleport_Node` is the exception: see
  "Teleporting one character" below.
- **Waiting.** A command waits while a dialogue is playing, while its gadget is busy, or while its conditions are not
  met, for up to an hour of game time. What can never work (no gadget of that name, the wrong kind of gadget, no
  acting character) fails at once.
- **Leaving the location** cancels the commands that were waiting there.

## During a battle

The game keeps updating the location's objects during a fight, so a command written mid-battle is taken at once, as
at any other time. What happens next depends on the command:

| Command | In a battle |
|---|---|
| `Dialog_Trigger`, `Roll_Trigger` | They set off the object's own dialogue or roll, which runs only when the object may run: one set to run outside battles only waits until the fight is over, one set to run in battles only runs now, one set to run always runs now. |
| `Teleport_Node`, `Teleport_PartyNode` | The character or the party is moved at once, in the middle of the fight. |
| `Torches_Lit`, `Torches_Auto`, `Torch_<name>` | The torches change at once. |
| `Clock_` read-outs and counters | They move with the rounds (six seconds each), the only way the clock moves in a battle. |
| `Clock_Skip...` | Never in a battle: the request fails (see "Clock" below). |

The objects whose flows write the variables follow the same rule: an object set to run outside battles only doesn't
run its flow during a fight, so it doesn't write its command until the fight is over.

## Who is the acting character

`Roll_Trigger` and `Teleport_Node` act for one particular character. The mod never guesses who:

| The variable was written by | Acting character |
|---|---|
| an Area Activator or pressure plate | the character whose move made it fire, whoever else stands in it |
| something a character interacted with | that character |
| a gadget that one of the above activated (its "Activate" target, for example a Variable Operation), through any number of gadgets | the character that set off the first gadget |
| a Success or Failure target of a Roll Activator, or a gadget one of them activated | the character who rolled |
| a Variable Activator or Quest Activator reacting to a variable or quest step that a character's gadget changed | that character |
| a line of a dialogue (its own action), or something reacting to what a dialogue's action changed | the character whose gadget started the dialogue: who talked to the NPC, who walked into the area that starts it, or the acting character of `Dialog_Trigger` |
| a Timed Activator that a character's gadget enabled (or whose time it reset) | that character, each time it fires, until something else enables or disables it |
| a button or other gadget set off by damage, by a spell or effect, by a revealed tag or by a theft | the party member or guest who did it |
| a timer that runs from the start, an Entry Activator, a quest the game advanced, the cheats panel, an enemy's hit, or a gadget activated by one of these | none: the command fails and says so |

- A chain can mix these in any number: an area activates a gadget that sets a variable, a Variable Activator
  reacts and advances a quest, a Quest Activator enables a Timed Activator, and the timer's target writes
  `Teleport_Node`. The character who walked into the area is teleported.
- When several characters arrive at once (the party entering the location, a party teleport), nobody "walked in".
  The command then only works if exactly one character is in the area. The same goes for a chain of activated
  gadgets whose start the mod couldn't trace to one character.
- A dialogue goes by who started it, not by who speaks: the game hands out the speaking roles itself.
- A timer keeps the character who set it going only while it stays as that character left it. Enabled or reset
  by something that has no character, or disabled, it has none any more.
- The mod's own writes are not links of a chain: a Variable Activator on `Roll_Trigger_Status`, on a `Clock_`
  variable or on a command variable being emptied starts a new chain with no acting character. Your own gadgets
  writing to those variables are ordinary writes, and carry their character.
- If your campaign uses one of the mod's names for something of its own, the mod keeps out of its way: see
  "When your campaign already uses one of these names" below.
- When two characters set off the same target before it has run, the game runs it once, for the first of them.
- When a command fails, a message at the top of the screen says so. For `Roll_Trigger` and `Teleport_Node` it also
  says what started it when that can't tell who acts (a timer, a Variable Activator, a quest step...). The mod's log
  (also in `Player.log`) has a line for each such write: the gadget that wrote it, the gadgets it went through, what
  started it, the characters the game passed along, and who acts or why nobody does.
- For `Roll_Trigger` the character must still be a conscious party member in that location when the command runs.
- A game saved while one of these commands was waiting, or in the middle of a chain, keeps its acting character:
  the mod writes who is behind each waiting command, each gadget that is set off but has not run yet, each timer a
  character set going and the queue of one-character teleports into the save, in a String variable named
  `Commands_SavedState` that exists in the save only. If your campaign has a variable of that name, the mod uses
  `Commands_SavedState_2` (and so on), and never reads or changes yours. What is lost over a save and load is
  what the game itself does not keep: a gadget that was in the middle of its steps (waiting before it writes, for
  instance) is not resumed, and an Area Activator or pressure plate that had not run yet is worked out again from
  who stands in it, so it only acts if exactly one character does.
- `Dialog_Trigger` uses the acting character when there is one, otherwise the first conscious party member.

Typical set-up for an automatic check: an Area Activator whose Variable Operation sets `Roll_Trigger` to the name of
the Roll Activator. Whoever walks in rolls. It works the same when the Area Activator instead activates a separate
Variable Operation gadget that sets `Roll_Trigger`.

## When your campaign already uses one of these names

A campaign made before these commands existed, or one that simply picked the same name, is not disturbed.

**Names only the mod writes** are the clock read-outs (`Clock_Hour`, `Clock_Minute`, `Clock_Day`,
`Clock_DayOfMonth`, `Clock_Month`, `Clock_Year`, `Clock_CampaignDay`), every `<command>_Status`, and the mod's own
`Clock_SkipRemainingSeconds` and `Clock_SkipTargetMinute`.

- If your campaign **writes** one of them itself (a Variable Operation gadget, or a Variable Operation among the
  actions of a dialogue line, a quest step or a quest outcome), the name is yours. The mod then writes to
  `<name>_2` instead, or `<name>_3`, and so on: the first one your campaign doesn't write either. Create that
  variable if you want what the mod writes.
- Reading one (a Variable Activator, a dialogue or quest condition) is what they are for, and changes nothing.
- A `_Status` name is also yours when you test it for a text the mod never writes (anything but `Pending`,
  `Running`, `Succeeded`, `Failed`, `Cancelled`). Setting a status back to an empty text from one place does not
  make it yours; writing any other text into it, or writing into it from more than one place, does.
- The counters (`Clock_MinutesElapsed`, `Clock_HoursElapsed`, `Clock_DaysElapsed`) are shared on purpose: the
  mod adds to them and you set them back, so they are never moved.
- This is worked out from the campaign each time a location loads, and `Player.log` has one line for each name
  moved, such as: ``DM names: `Clock_Hour` is the campaign's own; the mod writes its read-out to `Clock_Hour_2`.``

**Names you write to ask for something** (`Dialog_Trigger`, `Roll_Trigger`, `Teleport_Node`,
`Teleport_PartyNode`, `Dialog_ListGadgets`, the `Torches_` and `Torch_<name>` variables, the `Clock_Skip`
variables) can't be moved, since you are the one writing them. Instead:

- if the variable has a type the command never has (a command is a String, the others are Int), or
- if what is written into a command names nothing in the location and nothing anywhere in the campaign,

the mod takes the variable to be your own for the rest of that game, saves of it included: it does nothing, shows no message, leaves the
variable as it is, and says so once in `Player.log`. A command that names something real and can't be carried
out still fails with its message, as before. So a mistyped name is no longer reported while playing; the editor
points it out while you type (next section).

## Hints while you type, in the editor

- **A variable's name** that the mod uses shows a hint above the field, as you type it: what the mod does with
  that name, and, for a name the mod writes, the name it will use instead if your campaign writes that one
  itself. It appears in the window that creates or renames a campaign variable, and in the variable field of a
  gadget's parameters and of a dialogue's or quest's actions and conditions.
- **The text written into `Dialog_Trigger`, `Roll_Trigger`, `Teleport_Node` or `Teleport_PartyNode`** shows a
  warning above the field when nothing goes by that name, with the closest names. For a Variable Operation gadget
  the names are those of its own location (that is where it will write); for an action of a dialogue or a quest
  they are those of every location of the campaign. Capital and small letters don't matter, as in the commands.
- Hints show only while the field has the keyboard focus, and go when you click elsewhere. In the dialogue and
  quest editors they sit below the field, so they don't cover the row above.
- **Coloured names.** Wherever the editor shows a variable's name (the campaign's variable list, the variable
  picker, and the variable fields of gadgets, dialogues and quests), a name the mod uses is coloured:
  - **red:** the mod writes it itself, so anything your campaign keeps there is overwritten: the clock read-outs
    and counters, every `_Status` variable, and the mod's own clock-skip variables;
  - **blue:** writing it makes something happen: the commands (`Teleport_Node`, `Roll_Trigger`...), the requests
    (`Torches_Lit`, `Clock_SkipHours`...) and the counter switches (`Clock_Count...`).
- A hint only informs: nothing is changed or refused.

## The mod's version in campaign files

When the Dungeon Maker saves a campaign, or a location on its own, the mod adds its version to the file as one
extra field at the start (`"finishingBusinessVersion"`), for instance `1.18.0` or `1.18.0-dev.0330274b`.

- A game without the mod loads the file as before: the field is ignored. If that game saves the campaign again,
  the field is gone, so a file never claims a version that didn't write it.
- A development build carries the number of the release it is working towards and the commit it was built
  from, and is **older** than that release, as usual for a `-something` suffix: `1.17.0` < `1.17.1` <
  `1.18.0-dev.<commit>` < `1.18.0`. Two development builds with the same numbers count as the same version; only
  the commit tells them apart, and it doesn't say which is newer.
- Nothing is compared or refused yet. `Player.log` says which version a campaign file was saved with when the file
  is read and when the campaign is played.

## Teleporting one character

`Teleport_Node` works a little differently from the other commands.

- **Who:** the character that caused the activation, and never another. It must be a party member or a guest
  travelling with the party. If an enemy or another NPC sets the trigger off, the command fails and nobody moves,
  even with a party member standing in the same area. The character's state is not checked: unconscious, dying or
  dead, it is moved as it is. It only has to be in the location still.
- **Every activation is its own request.** Two characters stepping on two plates that both write the same Node
  name are both teleported, one after the other, in the order the activations happened. That holds even if the
  first plate's flow does something else first, such as a Wait, before it writes the variable. An activation keeps
  its place while its gadget is running another of its flows or can't run flows at the moment (a gadget set to
  run only outside or only inside battles); one whose conditions are not met when it is set off gives its place up,
  and its request joins the end of the queue if it comes later. A new request does not replace one that is
  waiting. Writing an empty text cancels all that are waiting, also when the variable already reads empty because
  the teleport before them has started.
- **Where:** the cell is picked by the same search the game's own Teleporter uses, so the character lands on a free
  cell at or near the gadget, facing the way the gadget does. If the search finds none, the command fails and
  nobody moves.
- A pressure plate that stays held down is one press. A second character stepping on it while the first is still
  there does not ask for a second teleport.

Typical set-up: an Area Activator one cell large whose Variable Operation sets `Teleport_Node` to the name of a Node.
Whoever walks onto that cell is moved to the Node, or next to it if the cell is taken.

`Teleport_PartyNode` moves the whole party and the guests travelling with it, placed in formation around the Node,
with summoned creatures moved along with the party. It needs no acting character, so a timer or a quest can use it.
Its status is `Succeeded` only when the game has placed every one of them, summoned creatures included, and `Failed`
if it found no free cell for someone; the log names who.

`Teleport_Node` moves the one character only. Its summoned creatures are not taken along: they stay where they are,
as with the game's own one-character Teleporter.

## Status

For any command you can create a **String** variable named `<command>_Status`, for example `Roll_Trigger_Status`.
The mod keeps it at one of:

| Value | Meaning |
|---|---|
| `Pending` | taken, waiting for something that can pass |
| `Running` | started: the dialogue is open, the roll is being made, the teleport is under way |
| `Succeeded` | done |
| `Failed` | could not be done; the reason is in the log |
| `Cancelled` | replaced by an empty text, the location was left, or the game was saved while it was `Running` and then loaded (a dialogue, roll or teleport can't be picked up again from a save) |

Use a Variable Activator on the status variable to react, for example to open a door when `Roll_Trigger_Status`
becomes `Succeeded`. Note that `Succeeded` means the roll was made, not that the character passed it: the Roll
Activator's own outcome links tell you that.

## Torches

Int variables: `1` = lit, `2` = out, `0` or missing = no opinion. For each torch the first of these that has an
opinion wins:

1. `Torch_<gadget name>`: that one torch, for example `Torch_TorchPike1`.
2. `Torches_Lit`: every torch in the location.
3. `Torches_Auto` (Int or Bool): when on, torches on **outdoor** maps are lit from 18:00 to 08:00 and out during the
   day. Indoor maps are not touched.

A state is applied once when it changes. A torch a player lights or puts out by hand stays that way until the
variable above it changes again. A torch is any gadget with "torch" in its name.

## Clock

All **Int** variables.

**Read-outs**, kept up to date by the mod:

| Variable | Value |
|---|---|
| `Clock_Hour` | 0 to 23 |
| `Clock_Minute` | 0 to 59 |
| `Clock_Day` | the day number the journal shows (1 on the first day) |
| `Clock_CampaignDay` | days since the campaign's start date (1 on the first day) |
| `Clock_DayOfMonth` | 0 to 29, as the game stores it |
| `Clock_Month` | 0 to 11, as the game stores it |
| `Clock_Year` | the year |

**Counters.** Each has a switch (Bool or Int) and an Int the mod adds to while the switch is on. Set the counter to 0
yourself to reset it.

| Switch | Counter | Counts |
|---|---|---|
| `Clock_CountMinutes` | `Clock_MinutesElapsed` | minutes passed |
| `Clock_CountHours` | `Clock_HoursElapsed` | hour boundaries passed |
| `Clock_CountDays` | `Clock_DaysElapsed` | midnights passed |

**Skipping time.** Write a number; the mod sets the variable back to 0 and advances the clock.

| Variable | Write | Result |
|---|---|---|
| `Clock_SkipHours` | 1 to 168 | that many hours on |
| `Clock_SkipDays` | 1 to 180 | that many days on, same time of day |
| `Clock_SkipToDay` | a campaign day, 1 to 180 days ahead | 00:00 of that day (numbered like `Clock_CampaignDay`) |
| `Clock_SkipToHour` | 1 to 23, or 24 for midnight | that hour, today or tomorrow if it has passed |

A value outside its range fails (status `Failed`, the reason in the log); it is not cut down to fit, and the others
written with it still run. A `Clock_SkipToHour` written with a `Clock_SkipToDay` that fails fails too.

- Requests written together are **one skip to one destination**, worked out in the order of the table. With
  `Clock_SkipToDay`, the hour is an hour of that day: `Clock_SkipToDay` 5 with `Clock_SkipToHour` 9 ends on day 5
  at 09:00, and with `Clock_SkipToHour` 24 on day 5 at 00:00.
- A long skip advances four hours at a time, a day per moment of play, so every dawn, new day and journal entry
  happens, and the counters count every minute, hour and day.
- **A skip is one wait,** as a long rest is. An object's "every N hours" or "after a time" trigger that comes due during it is set off
  once, when the skip ends, and counts its next interval from there: a two-hour trigger started at 08:00, then an
  eight-hour skip, goes off once at 16:00 and next at 18:00. The hours passed on the way are not played one by one,
  so a Variable Activator waiting for `Clock_Hour` to equal 10 isn't set off by a skip going past 10:00; it sees
  the hour the skip ends on. A counter, or a test such as "`Clock_Hour` at least 10", doesn't miss.
- **Never in battle.** Like a rest, a skip only happens outside a battle. A request written while a battle is in
  progress fails (status `Failed`), and a battle that starts while a skip is running ends it there (status
  `Cancelled`): the time already skipped stays skipped.
- A skip can't be cancelled once written (only a battle ends one). Each has an optional status variable, such as `Clock_SkipHours_Status`.
- While a skip runs, the mod keeps what is left of it, in seconds, in a variable of its own,
  `Clock_SkipRemainingSeconds`. It is saved with the game, so a game saved in the middle of a long skip finishes the
  skip when it is loaded, and a skip still running when the party leaves the location finishes in the next one.
  A skip to a given hour or day still ends at that hour, whatever time the journey between the two took; the mod
  keeps that moment in `Clock_SkipTargetMinute`. You can read both, but don't write them.

## Multiplayer

Nothing is sent between players' games. Every game sees the same variable being written by the same gadget and answers
it the same way, as it does for a gadget that triggers another gadget. This has **not been tested with two
computers** yet. Every player needs the same version of the mod.
