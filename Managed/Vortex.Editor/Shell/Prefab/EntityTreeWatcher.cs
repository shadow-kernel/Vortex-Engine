using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using Editor.ECS;
using Component = Editor.ECS.Component;

namespace VortexEditor.Shell.Prefab
{
    /// <summary>
    /// Watches an entity subtree (the entities, their components, the Children / Components collections) and raises
    /// one debounced <see cref="Changed"/> on the UI thread after any edit. Used by the Prefab Editor (dirty state +
    /// live preview) and by the inspector's prefab-instance bar (override count). Structural changes re-hook the
    /// subtree automatically; <see cref="Dispose"/> unhooks everything.
    /// </summary>
    public sealed class EntityTreeWatcher : IDisposable
    {
        private static readonly HashSet<string> Noise = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(GameEntity.IsSelected), nameof(GameEntity.IsExpanded), nameof(GameEntity.EntityId),
            nameof(GameEntity.IsHiddenInEditor), nameof(GameEntity.IsViewmodel), nameof(GameEntity.IsThirdPersonOnly),
            nameof(GameEntity.Parent), "DisplayName"
        };

        private readonly GameEntity _root;
        private readonly DispatcherTimer _debounce;
        private readonly HashSet<GameEntity> _entities = new HashSet<GameEntity>();
        private readonly HashSet<Component> _components = new HashSet<Component>();
        private bool _disposed;

        /// <summary>Raised (debounced, UI thread) after the subtree changed.</summary>
        public event Action Changed;
        /// <summary>Raised immediately (UI thread or caller thread) for structural changes (children / components added, removed, moved).</summary>
        public event Action StructureChanged;

        public GameEntity Root => _root;

        public EntityTreeWatcher(GameEntity root, int debounceMs = 150)
        {
            _root = root;
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, debounceMs)) };
            _debounce.Tick += (s, e) => { _debounce.Stop(); if (!_disposed) { try { Changed?.Invoke(); } catch { } } };
            if (root != null) Hook(root);
        }

        /// <summary>Signal a change that did not go through a property setter (e.g. a raw field write).</summary>
        public void Poke() { if (_disposed) return; _debounce.Stop(); _debounce.Start(); }

        private void Hook(GameEntity e)
        {
            if (e == null || !_entities.Add(e)) return;
            e.PropertyChanged += OnProperty;
            e.Components.CollectionChanged += OnComponents;
            e.Children.CollectionChanged += OnChildren;
            foreach (var c in e.Components) HookComponent(c);
            foreach (var ch in e.Children) Hook(ch);
        }

        private void HookComponent(Component c)
        {
            if (c == null || !_components.Add(c)) return;
            c.PropertyChanged += OnProperty;
        }

        private void UnhookAll()
        {
            foreach (var e in _entities)
            {
                e.PropertyChanged -= OnProperty;
                e.Components.CollectionChanged -= OnComponents;
                e.Children.CollectionChanged -= OnChildren;
            }
            foreach (var c in _components) c.PropertyChanged -= OnProperty;
            _entities.Clear();
            _components.Clear();
        }

        private void Rehook()
        {
            UnhookAll();
            if (_root != null && !_disposed) Hook(_root);
        }

        private void OnProperty(object sender, PropertyChangedEventArgs e)
        {
            if (e?.PropertyName != null && Noise.Contains(e.PropertyName)) return;
            Poke();
        }

        private void OnComponents(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null) foreach (var o in e.OldItems) if (o is Component c && _components.Remove(c)) c.PropertyChanged -= OnProperty;
            if (e.NewItems != null) foreach (var o in e.NewItems) if (o is Component c) HookComponent(c);
            if (e.Action == NotifyCollectionChangedAction.Reset) Rehook();
            try { StructureChanged?.Invoke(); } catch { }
            Poke();
        }

        private void OnChildren(object sender, NotifyCollectionChangedEventArgs e)
        {
            Rehook();
            try { StructureChanged?.Invoke(); } catch { }
            Poke();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _debounce.Stop();
            UnhookAll();
            Changed = null;
            StructureChanged = null;
        }
    }
}
