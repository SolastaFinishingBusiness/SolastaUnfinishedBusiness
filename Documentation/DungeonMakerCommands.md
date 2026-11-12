# Dungeon Maker campaign commands

A guide for campaign authors. With this mod, a Dungeon Maker campaign can read and move the clock, start a dialogue or
a skill check, teleport characters and switch torches by **writing campaign variables with reserved names**, for
example from an Area Activator's Variable Operation.

- You create the variables yourself in the campaign's variable list. Names are case-sensitive. A variable you don't
  create is ignored, so use only the ones you need.
- Players need the mod for any of this to work. Without it the variables are ordinary variables and nothing happens.
- The setting is Mod UI > Dungeon Maker > "Enable Dungeon Maker campaign commands" (on by default).
- The mod writes what it does, and why a command failed, to the mod's log.

Adapted from BlackguardRogue's Dungeon Maker tools.

## Commands

Each command is a **String** variable. Write a name into it; the mod sets it back to empty when the command starts or
ends, so you can write the same name again later.

| Variable | Write | What happens |
|---|---|---|
| `Dialog_Trigger` | the name of an NPC or object | Its dialogue starts, as if a character had clicked it. |
| `Roll_Trigger` | the name of a Roll Activator | The acting character rolls its check. |
| `Teleport_Node` | the name of a Node | The acting character is teleported onto the Node's exact cell. |
| `Teleport_PartyNode` | the name of a Node | The whole party, with its guests, is teleported there in formation. |

- **Names.** For `Dialog_Trigger` you can use the name the player sees, the name you typed when creating the NPC, or
  the gadget's own name (such as `NpcDialog4`). For the others use the gadget's own name. Capital letters don't
  matter. If a name fits more than one gadget, the command fails and the log lists them.
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

## Who is the acting character

`Roll_Trigger` and `Teleport_Node` act for one particular character. The mod never guesses who:

| The variable was written by | Acting character |
|---|---|
| an Area Activator or pressure plate | the character whose move made it fire, whoever else stands in it |
| something a character interacted with | that character |
| a timer, a variable or condition activator, a quest, a dialogue | none: the command fails and says so |

- When several characters arrive at once (the party entering the location, a party teleport), nobody "walked in".
  The command then only works if exactly one character is in the area.
- For `Roll_Trigger` the character must still be a conscious party member in that location when the command runs.
- A game saved while one of these commands was waiting loads without its acting character, so the command fails after
  loading. `Dialog_Trigger` and `Teleport_PartyNode` are not affected.
- `Dialog_Trigger` uses the acting character when there is one, otherwise the first conscious party member.

Typical set-up for an automatic check: an Area Activator whose Variable Operation sets `Roll_Trigger` to the name of
the Roll Activator. Whoever walks in rolls.

## Teleporting one character

`Teleport_Node` works a little differently from the other commands.

- **Who:** the character that caused the activation, and never another. It must be a party member or a guest
  travelling with the party. If an enemy or another NPC sets the trigger off, the command fails and nobody moves,
  even with a party member standing in the same area. The character's state is not checked: unconscious, dying or
  dead, it is moved as it is. It only has to be in the location still.
- **Every activation is its own request.** Two characters stepping on two plates that both write the same Node
  name are both teleported, one after the other, in the order the activations happened. A new request does not
  replace one that is waiting. Writing an empty text cancels all that are waiting.
- **Where:** exactly on the Node's cell, at its height. No nearby free cell is looked for. So a character can arrive
  on a cell another hero stands on, in mid-air or in a hazard. What follows is up to the game: over a hole the
  character falls.
- A pressure plate that stays held down is one press. A second character stepping on it while the first is still
  there does not ask for a second teleport.

Typical set-up: an Area Activator one cell large whose Variable Operation sets `Teleport_Node` to the name of a Node.
Whoever walks onto that cell is moved to the Node.

`Teleport_PartyNode` moves the whole party and the guests travelling with it, placed in formation around the Node,
with summoned creatures following their owners. It needs no acting character, so a timer or a quest can use it. Its
status is `Succeeded` only when every one of them is confirmed near the Node, and `Failed` if the game found no
free cell for someone.

## Status

For any command you can create a **String** variable named `<command>_Status`, for example `Roll_Trigger_Status`.
The mod keeps it at one of:

| Value | Meaning |
|---|---|
| `Pending` | taken, waiting for something that can pass |
| `Running` | started: the dialogue is open, the roll is being made, the teleport is under way |
| `Succeeded` | done |
| `Failed` | could not be done; the reason is in the log |
| `Cancelled` | replaced by an empty text, or the location was left |

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

- Requests written together are **one skip to one destination**, worked out in the order of the table. So
  `Clock_SkipToDay` 5 with `Clock_SkipToHour` 9 ends on day 5 at 09:00.
- A long skip advances a few hours at a time over a moment of play, so every dawn, new day and journal entry happens.
- A skip can't be cancelled once written. Each has an optional status variable, such as `Clock_SkipHours_Status`.

## Multiplayer

Nothing is sent between players' games. Every game sees the same variable being written by the same gadget and answers
it the same way, as it does for a gadget that triggers another gadget. This has **not been tested with two
computers** yet. Every player needs the same version of the mod.
