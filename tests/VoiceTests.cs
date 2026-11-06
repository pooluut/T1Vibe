using System;

class VoiceTests
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static void Main()
    {
        var state = new Bridge.VoiceState();
        Require(state.Update(2, true), "Initial voice press must trigger");
        for (int i = 0; i < 100; i++)
            Require(!state.Update(2, true), "Held voice key must not toggle dictation again");
        Require(!state.Update(3, false), "Unrelated report release must not trigger");
        Require(!state.Update(2, true), "Unrelated report must not release voice");
        Require(!state.Update(2, false), "Voice release must not trigger");
        Require(state.Update(2, true), "Second voice press must trigger");
        Require(new Bridge.VoiceState().Update(2, true), "Devices must have independent key states");
        var alternate = new Bridge.VoiceState();
        Require(alternate.Update(7, true), "Descriptor report IDs must not be hard-coded");
        Require(!alternate.Update(7, false), "Alternate report release must not trigger");
        Require(alternate.Update(7, true), "Alternate report must work on next press");

        ushort[] usages;
        Require(Bridge.LegacyConsumerUsages(new byte[] { 2, 0xcf, 0 }, out usages)
            && usages.Length == 1 && usages[0] == 0xcf, "Known T1 voice report must remain compatible");
        Require(Bridge.LegacyConsumerUsages(new byte[] { 2, 0, 0 }, out usages)
            && usages[0] == 0, "Known release report must clear held state");
        Require(Bridge.LegacyConsumerUsages(new byte[] { 2, 0x24, 2 }, out usages)
            && usages[0] == 0x224, "Navigation usage must retain little-endian decoding");
        Require(!Bridge.LegacyConsumerUsages(new byte[] { 3, 0xcf, 0 }, out usages), "Unknown report needs its descriptor");
        Require(!Bridge.LegacyConsumerUsages(new byte[] { 2, 0xcf }, out usages), "Truncated report must be rejected");
        Require(!Bridge.LegacyConsumerUsages(new byte[] { 2, 0xcf, 0, 0 }, out usages), "Unknown padded format must not be guessed");
        Require(!Bridge.LegacyConsumerUsages(null, out usages), "Missing report must be rejected");

        Require(Bridge.ValidReports(3, 2, 6), "Batched press/release reports must be accepted");
        Require(!Bridge.ValidReports(3, 2, 5), "Truncated batch must be rejected");
        Require(!Bridge.ValidReports(0, 1, 3), "Empty report must be rejected");
        Require(!Bridge.ValidReports(3, -1, 3), "Negative count must be rejected");
        Require(!Bridge.ValidReports(int.MaxValue, int.MaxValue, 16), "Report size multiplication must not overflow");

        var remote = new Bridge.RemoteVoiceKey();
        IntPtr chat = new IntPtr(123), anotherChat = new IntPtr(456); bool trigger;
        Require(!remote.Filter(0x78, 0x100, IntPtr.Zero, out trigger) && !trigger, "F9 outside ChatGPT must pass through");
        Require(!remote.Filter(0x78, 0x101, chat, out trigger) && !trigger, "Unclaimed F9 release must not start dictation");
        Require(!remote.Filter(0x41, 0x100, chat, out trigger), "Remote handler must not claim normal keys");
        for (int i = 0; i < 10; i++) Require(remote.Filter(0x78, 0x100, chat, out trigger) && !trigger, "F9 down/repeats must be consumed without repeated dictation");
        Require(remote.Filter(0x78, 0x101, chat, out trigger) && trigger, "Forwarded F9 must trigger once on release");
        Require(!remote.Filter(0x78, 0x101, chat, out trigger) && !trigger, "Orphan release must not retrigger");
        Require(remote.Filter(0x78, 0x104, chat, out trigger) && !trigger, "System key down must be supported");
        Require(remote.Filter(0x78, 0x105, chat, out trigger) && trigger, "System key up must be supported");
        remote.Filter(0x78, 0x100, chat, out trigger);
        Require(remote.Filter(0x78, 0x101, anotherChat, out trigger) && !trigger, "Focus change must cancel dictation");
        remote.Filter(0x78, 0x100, chat, out trigger);
        Require(remote.Filter(0x78, 0x101, IntPtr.Zero, out trigger) && !trigger, "Leaving ChatGPT must cancel dictation");

        Require(Bridge.RelayForegroundAllowed("GameViewer") && Bridge.RelayForegroundAllowed("gameviewer"), "UU foreground process must be supported");
        Require(!Bridge.RelayForegroundAllowed("GameViewerServer") && !Bridge.RelayForegroundAllowed("ChatGPT") && !Bridge.RelayForegroundAllowed(null), "Relay must not inject into unrelated windows");
        var pulse = new Bridge.RelayPulse(); var emitted = new System.Collections.Generic.List<bool>();
        Func<bool, bool> emit = delegate(bool down) { emitted.Add(down); return true; };
        Require(!pulse.TryStart(false, emit) && emitted.Count == 0, "Unfocused relay must send no keys");
        Require(pulse.TryStart(true, emit) && pulse.Held, "Relay must submit a down event");
        Require(!pulse.TryStart(true, emit) && emitted.Count == 1, "Pending pulse must not submit another down event");
        Require(pulse.Release(emit) && !pulse.Held && emitted.Count == 2 && emitted[0] && !emitted[1], "Relay must pair down/up across timer ticks");
        Require(pulse.Release(emit) && emitted.Count == 2, "Cleanup must not create orphan releases");
        Require(!pulse.TryStart(true, delegate { return false; }) && !pulse.Held, "Failed down must not enter held state");
        Require(pulse.TryStart(true, emit), "Next pulse must work after failure");
        Require(!pulse.Release(delegate { return false; }) && pulse.Held, "Failed up must retain state for retry");
        Require(pulse.Release(emit) && !pulse.Held, "Retry/exit must release a pending key");
        Console.WriteLine("PASS: voice reports, UU foreground gate, forwarding pulse, F9 receiver, repeats, focus changes and send failures.");
    }
}
