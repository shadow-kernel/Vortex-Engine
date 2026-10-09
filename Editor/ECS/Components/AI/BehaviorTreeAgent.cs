using System;
using System.Runtime.Serialization;

namespace Editor.ECS.Components.AI
{
    /// <summary>
    /// Runs a behaviour tree asset (<c>.vbt</c>) on the entity (#111): from play start when <see cref="RunOnStart"/>,
    /// or when a script calls <c>BehaviorTree.Run</c>. The tree's tasks drive the entity's Nav Agent, read its
    /// AI Perception and talk to its scripts through the blackboard and <c>OnMessage</c>.
    /// </summary>
    [DataContract(Name = "BehaviorTreeAgent", Namespace = "")]
    public class BehaviorTreeAgent : Component
    {
        private string _treePath = "";
        private bool _runOnStart = true;
        private float _tickInterval;
        private bool _logTasks;

        public override string DisplayName => "Behavior Tree";
        public override string IconCode => "";   // MDL2 "Flow"
        public override string IconColor => "#C586C0";

        /// <summary>Project-relative path of the tree (<c>Assets/AI/Monster.vbt</c>).</summary>
        [DataMember(Name = "treePath", Order = 10)]
        public string TreePath { get => _treePath; set => SetProperty(ref _treePath, value ?? "", nameof(TreePath)); }

        /// <summary>Start the tree with the scene (off = a script starts it with <c>BehaviorTree.Run</c>).</summary>
        [DataMember(Name = "runOnStart", Order = 11)]
        public bool RunOnStart { get => _runOnStart; set => SetProperty(ref _runOnStart, value, nameof(RunOnStart)); }

        /// <summary>Seconds between ticks (0 = every frame). Crowds of agents can tick at 10 Hz without anyone noticing.</summary>
        [DataMember(Name = "tickInterval", Order = 12)]
        public float TickInterval { get => _tickInterval; set => SetProperty(ref _tickInterval, float.IsNaN(value) ? 0f : Math.Max(0f, Math.Min(5f, value)), nameof(TickInterval)); }

        /// <summary>Log every task start / end to the console (debugging).</summary>
        [DataMember(Name = "logTasks", Order = 13)]
        public bool LogTasks { get => _logTasks; set => SetProperty(ref _logTasks, value, nameof(LogTasks)); }

        public BehaviorTreeAgent() : base() { }
        public BehaviorTreeAgent(GameEntity entity) : base(entity) { }
    }
}
