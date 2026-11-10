# Working in this repo with an AI agent

- Before you edit a path, claim it: `sankofa claim <path>`.
- Before you stop, leave a note: `sankofa note "<what changed; what is open>"`.
- If a step needs the human (sudo, login, a decision), record it:
  `sankofa awaiting "<what you need>"`.
- Not sure who owns a path? `sankofa ask <path>`.
- Hooks enforce these rules. The commands are the interface; do not edit
  `.sankofa/` by hand.
