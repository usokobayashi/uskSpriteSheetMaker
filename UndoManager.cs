using System;
using System.Collections.Generic;

namespace SpriteSheetMaker
{
    // 変更のたびに丸ごとスナップショットを取り、変化があればUndoスタックへ積む
    // 汎用的なUndo/Redo管理。どのフィールドを持つか（AppStateSnapshotの中身）や
    // それをUIへどう反映するかはMainForm側の責務のままとし、ここでは
    // 「いつ・どちらへ積むか」というスタック操作と直前状態の追跡だけを扱う。
    internal sealed class UndoManager<TState> where TState : class
    {
        private readonly Func<TState> captureState;
        private readonly Action<TState> restoreState;
        private readonly Func<TState, TState, bool> statesEqual;
        private readonly Stack<TState> undoStack = new Stack<TState>();
        private readonly Stack<TState> redoStack = new Stack<TState>();
        private TState lastCommittedState;

        public UndoManager(Func<TState> captureState, Action<TState> restoreState, Func<TState, TState, bool> statesEqual)
        {
            this.captureState = captureState;
            this.restoreState = restoreState;
            this.statesEqual = statesEqual;
        }

        public bool CanUndo { get { return undoStack.Count > 0; } }
        public bool CanRedo { get { return redoStack.Count > 0; } }

        // 現在の状態を基準点として記録し、Undo/Redo履歴をクリアする。
        // コンストラクタでUIが組み上がった直後に一度だけ呼ぶ。
        public void Initialize()
        {
            undoStack.Clear();
            redoStack.Clear();
            lastCommittedState = captureState();
        }

        // 直前に記録した状態と比較し、変化していればUndoスタックへ積む。
        // 新しい変更が確定するので、Redo履歴は破棄する。
        // 変化がなければ何もしない（連打やスライダーの微調整で無駄に積まない）。
        public bool CommitIfChanged()
        {
            TState current = captureState();
            if (lastCommittedState == null)
            {
                lastCommittedState = current;
                return false;
            }
            if (statesEqual(lastCommittedState, current)) return false;

            undoStack.Push(lastCommittedState);
            redoStack.Clear();
            lastCommittedState = current;
            return true;
        }

        // 操作ではなく、画面の都合で自動に整えられた変化（並び情報の整理など）を、記録済みの状態へ取り込む。
        // 別の1回として積まないため（積むと、元に戻すを2回押さないと戻らない）。
        public void Resettle()
        {
            if (lastCommittedState != null) lastCommittedState = captureState();
        }

        public bool Undo()
        {
            if (undoStack.Count == 0) return false;
            TState leavingState = lastCommittedState;
            TState target = undoStack.Pop();
            restoreState(target);
            // 復元中のクランプ処理などでUIが実際に落ち着いた値へ再同期する。
            TState settled = captureState();
            redoStack.Push(leavingState);
            lastCommittedState = settled;
            return true;
        }

        public bool Redo()
        {
            if (redoStack.Count == 0) return false;
            TState leavingState = lastCommittedState;
            TState target = redoStack.Pop();
            restoreState(target);
            TState settled = captureState();
            undoStack.Push(leavingState);
            lastCommittedState = settled;
            return true;
        }
    }
}
