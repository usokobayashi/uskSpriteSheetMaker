using System.Collections.Generic;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class UndoManagerTests
    {
        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "UndoManager_CommitIfChanged_OnlyPushesOnRealChange", Action = CommitIfChanged_OnlyPushesOnRealChange };
            yield return new TestCase { Name = "UndoManager_UndoRedo_RoundTripsThroughHistory", Action = UndoRedo_RoundTripsThroughHistory };
            yield return new TestCase { Name = "UndoManager_NewCommitAfterUndo_ClearsRedoHistory", Action = NewCommitAfterUndo_ClearsRedoHistory };
            yield return new TestCase { Name = "UndoManager_UndoRedo_OnEmptyHistoryIsNoOp", Action = UndoRedo_OnEmptyHistoryIsNoOp };
        }

        private sealed class MutableState
        {
            public int Value;
        }

        private sealed class Snapshot
        {
            public int Value;
        }

        private static UndoManager<Snapshot> MakeManager(MutableState state)
        {
            return new UndoManager<Snapshot>(
                () => new Snapshot { Value = state.Value },
                snapshot => state.Value = snapshot.Value,
                (a, b) => a.Value == b.Value);
        }

        private static void CommitIfChanged_OnlyPushesOnRealChange()
        {
            var state = new MutableState { Value = 0 };
            var manager = MakeManager(state);
            manager.Initialize();
            Assert.IsFalse(manager.CanUndo, "no history right after Initialize");

            Assert.IsFalse(manager.CommitIfChanged(), "committing with no change should be a no-op");
            Assert.IsFalse(manager.CanUndo, "still no history after a no-op commit");

            state.Value = 1;
            Assert.IsTrue(manager.CommitIfChanged(), "committing an actual change should push history");
            Assert.IsTrue(manager.CanUndo, "history should exist after a real change");

            Assert.IsFalse(manager.CommitIfChanged(), "committing again with the same value should not push twice");
        }

        private static void UndoRedo_RoundTripsThroughHistory()
        {
            var state = new MutableState { Value = 0 };
            var manager = MakeManager(state);
            manager.Initialize();

            state.Value = 1;
            manager.CommitIfChanged();
            state.Value = 2;
            manager.CommitIfChanged();

            Assert.IsTrue(manager.Undo(), "first undo should succeed");
            Assert.AreEqual(1, state.Value, "first undo should restore the previous value");
            Assert.IsTrue(manager.CanRedo, "undo should make redo available");

            Assert.IsTrue(manager.Undo(), "second undo should succeed");
            Assert.AreEqual(0, state.Value, "second undo should restore the initial value");
            Assert.IsFalse(manager.CanUndo, "no more undo history left");

            Assert.IsTrue(manager.Redo(), "first redo should succeed");
            Assert.AreEqual(1, state.Value, "first redo should restore the intermediate value");

            Assert.IsTrue(manager.Redo(), "second redo should succeed");
            Assert.AreEqual(2, state.Value, "second redo should restore the latest value");
            Assert.IsFalse(manager.CanRedo, "no more redo history left");
        }

        private static void NewCommitAfterUndo_ClearsRedoHistory()
        {
            var state = new MutableState { Value = 0 };
            var manager = MakeManager(state);
            manager.Initialize();

            state.Value = 1;
            manager.CommitIfChanged();
            manager.Undo();
            Assert.IsTrue(manager.CanRedo, "redo should be available right after undo");

            state.Value = 99;
            manager.CommitIfChanged();
            Assert.IsFalse(manager.CanRedo, "committing a new change after undo should discard the old redo history");
        }

        private static void UndoRedo_OnEmptyHistoryIsNoOp()
        {
            var state = new MutableState { Value = 5 };
            var manager = MakeManager(state);
            manager.Initialize();

            Assert.IsFalse(manager.Undo(), "undo with no history should return false");
            Assert.AreEqual(5, state.Value, "undo with no history should not touch the state");
            Assert.IsFalse(manager.Redo(), "redo with no history should return false");
            Assert.AreEqual(5, state.Value, "redo with no history should not touch the state");
        }
    }
}
