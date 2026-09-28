---
description: One-screen brief before meeting someone, from my People Journal
argument-hint: <name or role>
allowed-tools: mcp__people-journal__journal_get_people, mcp__people-journal__journal_search_entries, mcp__people-journal__journal_get_entry
---
Prepare me to meet: $ARGUMENTS

1. Call journal_get_people with that name.
   - Nobody matches: say so, call it again without a name, and list the closest names instead of guessing.
   - Several people match: ask which one before going on.
2. Read the full text of their latest 3-5 entries with journal_get_entry. If journal_get_people says there
   are older ones, search with journal_search_entries (person = their name) only if it helps.

Then give me a **one-screen brief**: tight, scannable, no padding. Refer to them by name or "they";
use he/she only if my own notes about them do. Use only what the journal says. Never invent facts,
motives or personality; if something seems worth knowing, turn it into a question instead.

- **Who**: name, title, relationship, key person or not.
- **Background** (only if there is LinkedIn background; otherwise skip): 2-3 lines on their current role,
  the path that got them here, and any real overlap with me that I could use to connect, if my notes
  show one. If it was imported more than 6 months ago, add "(LinkedIn imported <date>; may be out of date.)"
- **What I know about them**: the notes that have content.
- **History**: their latest 3-5 entries, newest first, as date + one-line gist.
- **Open commitments**: both directions, with due dates. Flag overdue ones first.
- **Questions worth asking**: 3-5, suited to the relationship (boss, exec, peer, direct report...).
  Aim them at gaps: an empty note (e.g. nothing on how they like information) is a question to ask.
  Skip anything the entries show I've already covered.

If there are no entries with them yet, say plainly at the top that this is a first meeting, and make the
questions about listening and learning rather than proposing anything.

If the journal can't find the person but the name looks right, remind me I can add them in my journal.
