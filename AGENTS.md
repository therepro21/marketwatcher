# MarketWatcher project rules

- Continue authorized implementation until the requested outcome is implemented, checked, and installed. Do not stop at a partial fix or a plan; report concrete blockers if completion requires missing authority or information.
- Never launch the installed MarketWatcher application as a child of Codex. Dedicated isolated diagnostic/test runners are allowed.
- Preserve and back up local searches, recipients, browser sessions, and exclusion data during updates. Never commit private state or credentials.
- WhatsApp photo notifications must contain the actual image and caption as one media message.
- Never automatically resend a message merely because a UI element or delivery confirmation is missing. Persist its message ID before dispatch, reconcile that ID, and hold uncertain sends for review.
- Clean only Edge processes whose user-data-dir exactly equals MarketWatcher's profile. Never terminate Sync3D, another application's profile, or the user's normal Edge.
