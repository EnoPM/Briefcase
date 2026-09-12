# Briefcase.Unreal.Invocation

This static library owns prepared Unreal calls:

- direct reflected `ProcessEvent` invocation and object-handle translation;
- immutable prepared function and property descriptors;
- bounded, tagged tokens that never expose native addresses;
- game-thread enforcement for reflected invocation;
- canonical and BVC1 argument conversion around `ProcessEvent`;
- lifetime-safe initialization and destruction of owning parameters;
- owned BVC1 output buffers and their release contract.

Patch hooks, delegate subscriptions, and managed scheduling remain in the runtime
or their future dedicated modules.
