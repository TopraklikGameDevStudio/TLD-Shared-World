namespace TLDCoop
{
    /// <summary>Message ids. Every packet starts with one byte from this list.</summary>
    internal enum Msg : byte
    {
        Hello = 1,        // client -> host: modVersion, gameVersion, name
        Welcome = 2,      // host -> client: yourId, hostName
        Reject = 3,       // host -> client: reason
        PlayerJoined = 12, // host -> all: id, name
        PlayerLeft = 11,  // host -> all: id
        PlayerState = 10, // both ways: id, scene, x, y, z, yaw, crouch, held light, away, sleep (0 awake, 1 sleeping, 2 passing time), downed (0 up, 1 on the ground), seconds left on the ground, where they look (signed byte, degrees, down is positive; since 0.10.0)
        WorldTime = 20,   // host -> clients: elapsedHours, normalizedTime, day
        Weather = 21,     // host -> clients: serialized weather, stage
        TimeAccel = 22,   // host -> clients: does everyone sleep?, speed factor (0 = your own), host clock hours, who is awake (255 nobody)
        RequestWorld = 30,  // client -> host
        WorldData = 31,     // host -> client: save file bytes
        WorldNotReady = 32, // host -> client: reason
        ItemRemoved = 40,   // scene, item ref
        ItemSpawned = 41,   // scene, guid, prefab, pos, rot, hp, units
        ContainerState = 42,// scene, guid, pos, serialized container (first look only)
        ContainerItemRemoved = 43, // scene, container guid, pos, prefab, instance id, hp, units
        ContainerItemAdded = 44,   // same
        AnimalInfo = 60,           // host -> clients: scene, id, guid, spawn region guid + pos, prefab, wildlife mode
        AnimalStates = 61,         // host -> clients (unreliable): scene, n x (id, pos, yaw, ai mode, owner player, target (player id, 254 other, 255 none), hp)
        AnimalGone = 62,           // host -> clients: scene, id
        AnimalReact = 64,          // to the animal's owner (a client's goes through the host): scene, id, ai mode wanted (flee/stunned), player pos
        AnimalDamage = 63,         // to the animal's owner (a client's goes through the host): scene, id, damage, bleed minutes, source, collider, weapon
        AnimalOwner = 65,          // host -> one client: scene, id, round, what (1 it is yours now, 2 give it back, 3 taken back, 4 no), detail, reason, the animal's state
        AnimalOwnerNote = 66,      // client -> host: scene, id, round, what (1 took it, 2 could not, 3 here it is back, 4 busy: keeping it, 5 I want it (test), 6 it died here, 7 it noticed my player), detail, reason, state
        AnimalOwnerStates = 67,    // owner client -> host (unreliable): scene, n x (id, pos, yaw, ai mode, target, hp, wounds)
        FireState = 50,            // scene, guid, prefab address, pos, rot, serialized fire
        CarcassState = 51,         // scene, animal kind, pos, serialized carcass
        CarcassGone = 52,
        SceneAsk = 70,             // client -> host: ask id, region save name, region name, hash of the copy it holds ("I am about to load this: who has the newest?")
        SceneAnswer = 71,          // host -> client: ask id, save name, verdict (0 nobody: you make it, 1 wait, 2 off, 3 copy is coming, 4 yours is the newest), bytes
        SceneBlob = 72,            // both ways: kind, flags, ask/request id, source player, save name, region name, length, packed region entry
        SceneLive = 73,            // host -> client: request id, save name ("you stand there: send its state now")
        SceneHere = 74,            // client -> host: save name of the region it now stands in ("" = left), region name
        SceneLiveNo = 75,          // client -> host: request id, save name ("I am not standing there")
        CarcassRequest = 53,       // client -> host: scene, kind, pos, guid (what is left on it?)          // scene, animal kind, pos
        Revive = 80,               // helper -> downed player (a client's goes through the host): target id, helper id ("I lifted you")
        DownedNote = 81,           // downed player -> everyone (host relays): id, what (2 lifted, 3 died the co-op way), helper id or reason, scene, pos
        Whistle = 82,              // whistler -> everyone (host relays): id, scene, pos
        HandOffer = 83,            // giver -> receiver (a client's goes through the host): receiver id, giver id, hand-over number, scene, giver pos, prefab, condition, units, lit light, light state, the item as the game saves it
        PlayerHit = 85,
        MapSurvey = 86,
        Voice = 87,                // talker -> everyone (host relays, unreliable sequenced): id, scene, seq, mu-law 16 kHz frame            // mapper -> everyone (host relays): id, scene, pos (finished a charcoal survey there)            // shooter -> target (a client's goes through the host): target id, shooter id, scene, damage %, weapon, head
        HandAnswer = 84,           // receiver -> giver (a client's goes through the host): giver id, receiver id, hand-over number, answer (1 taken, else why not)
        Look = 88,                 // player -> everyone (host relays under the sender's real id, and tells a newcomer every look it knows): id, flags (1 hat, 2 scarf, 4 gloves), 6 x RGB (hat, scarf, torso, hands, legs, boots)
        Wind = 89,                 // host -> clients: serialized wind (the client's own wind does not change phase on its own)
        MapReveal = 90,            // revealer -> everyone (host relays): id, kind (1 postcard/polaroid, 2 region map), key, flag
        GroundRequest = 91,        // client -> host: scene (send me your loose items there)
        GroundList = 92,           // host -> client: scene, count (-1 = not there yet), per item guid, game id, prefab, pos, rot, hp, units, lit
    }

    internal static class Protocol
    {
        public const string ConnectKey = "TLDCoop-proto-2";
        public const byte HostId = 0;
    }
}
