using SessionCli;

// Headless counterpart to the WPF app: reads ~/.claude/projects and drives the same
// actions the app drives from a keystroke — marking, forking, restarting, resuming —
// so an agent, a script or a scheduled task can do everything a window can.
//
// It shares SessionCore with the app, so both agree on how a session's status is
// classified, when a restart is safe, and where the operator's marks live. There is no
// second implementation of any of it to drift.
//
// `SessionCli` with no verb, and the old flag-only forms, still emit exactly the JSON
// they always did: the morning brief runs `SessionCli --json <path>` on a schedule from a
// sandbox that cannot read ~/.claude/projects itself, and must not notice verbs happened.
//
// Run `SessionCli help` for the full surface.

return Cli.Run(args);
