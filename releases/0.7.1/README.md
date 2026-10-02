v0.7.1: Match recap survives a reconnect

A quick fix on top of v0.7.0, for the match recap that arrived with it.

### Reconnecting no longer cuts the recap short
- **What you saw**: if Hearthstone disconnected or restarted in the middle of a match and you rejoined, the recap at the end counted only the turns after the rejoin. A 15-turn match could read "4 turns", with APM and damage for those last turns only.
- **Why**: Hearthstone Deck Tracker announces a rejoin the same way it announces a new match, and the recap started over.
- **Fixed**: the recap keeps what it had recorded and carries on, including the turn you rejoined in. The header shows how many turns the match really ran.

### Good to know
- A combat that plays out while you are disconnected is not counted in the damage figures: the plugin never saw it.
- A recap that v0.7.0 already saved from a reconnected match is not repaired. It stays in your history as it was recorded.
