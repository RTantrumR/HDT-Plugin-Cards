v0.6.1: Card art for the new season

A quick fix on top of v0.6.0. If you updated to v0.6.0 and the Aberrations and other new cards show up in the search with no picture, this is the release for you.

### Card art loads again for new cards
- **What you saw**: the new season's cards appeared in the search, but with a blank frame instead of art.
- **Why**: the plugin fetches art it does not have from hsbg.cards one card at a time. That download had been silently failing since the website moved to WebP-only images in September, hidden until now by the bulk art pack, which already covered every card. The new season's cards were the first with no pack art.
- **Fixed**: new cards download their art on first view, and the startup art sync picks up changed and missing art again. On the first launch after installing, the sync fetches everything the pack was missing (including golden hero portraits), so give it a few seconds before opening the search.

### Under the hood
- The art sync now checks what is actually on disk, not only its own bookkeeping, and marks itself fully caught up only when it is.
- The sync waits for the card-data refresh to finish before it runs, so a launch that brings new cards also brings their art.
- A golden image the website lists but has never rendered no longer counts as a failed download every launch.

### Aberrations, one day in
- Verified in a live match: the opponent minion-type icons read Aberration boards correctly, and the leaderboard ordering holds.
- The plugin loads and runs on Hearthstone Deck Tracker 1.58.x, which shipped the same day as the season.
