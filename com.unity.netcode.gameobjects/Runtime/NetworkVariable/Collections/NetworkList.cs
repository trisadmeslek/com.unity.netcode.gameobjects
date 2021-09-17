using System;
using System.Collections.Generic;
using Unity.Collections;

namespace Unity.Netcode
{
    /// <summary>
    /// Event based NetworkVariable container for syncing Lists
    /// </summary>
    /// <typeparam name="T">The type for the list</typeparam>
    public class NetworkList<T> : NetworkVariableBase where T : unmanaged, IEquatable<T>
    {
        private NativeList<T> m_List = new NativeList<T>(64, Allocator.Persistent);
        private NativeList<NetworkListEvent<T>> m_DirtyEvents = new NativeList<NetworkListEvent<T>>(64, Allocator.Persistent);

        /// <summary>
        /// Delegate type for list changed event
        /// </summary>
        /// <param name="changeEvent">Struct containing information about the change event</param>
        public delegate void OnListChangedDelegate(NetworkListEvent<T> changeEvent);

        /// <summary>
        /// The callback to be invoked when the list gets changed
        /// </summary>
        public event OnListChangedDelegate OnListChanged;

        /// <summary>
        /// Creates a NetworkList with the default value and settings
        /// </summary>
        public NetworkList() { }

        /// <summary>
        /// Creates a NetworkList with the default value and custom settings
        /// </summary>
        /// <param name="readPerm">The read permission to use for the NetworkList</param>
        /// <param name="values">The initial value to use for the NetworkList</param>
        public NetworkList(NetworkVariableReadPermission readPerm, IEnumerable<T> values) : base(readPerm)
        {
            foreach (var value in values)
            {
                m_List.Add(value);
            }
        }

        /// <summary>
        /// Creates a NetworkList with a custom value and the default settings
        /// </summary>
        /// <param name="values">The initial value to use for the NetworkList</param>
        public NetworkList(IEnumerable<T> values)
        {
            foreach (var value in values)
            {
                m_List.Add(value);

            }
        }

        /// <inheritdoc />
        public override void ResetDirty()
        {
            base.ResetDirty();
            m_DirtyEvents.Clear();
        }

        /// <inheritdoc />
        public override bool IsDirty()
        {
            // we call the base class to allow the SetDirty() mechanism to work
            return base.IsDirty() || m_DirtyEvents.Length > 0;
        }

        /// <inheritdoc />
        public override void WriteDelta(ref FastBufferWriter writer)
        {

            if (base.IsDirty())
            {
                writer.WriteValueSafe((ushort)1);
                writer.WriteValueSafe(NetworkListEvent<T>.EventType.Full);
                WriteField(ref writer);

                return;
            }

            writer.WriteValueSafe((ushort)m_DirtyEvents.Length);
            for (int i = 0; i < m_DirtyEvents.Length; i++)
            {
                writer.WriteValueSafe(m_DirtyEvents[i].Type);
                switch (m_DirtyEvents[i].Type)
                {
                    case NetworkListEvent<T>.EventType.Add:
                        {
                            writer.WriteValueSafe(m_DirtyEvents[i].Value);
                        }
                        break;
                    case NetworkListEvent<T>.EventType.Insert:
                        {
                            writer.WriteValueSafe(m_DirtyEvents[i].Index);
                            writer.WriteValueSafe(m_DirtyEvents[i].Value);
                        }
                        break;
                    case NetworkListEvent<T>.EventType.Remove:
                        {
                            writer.WriteValueSafe(m_DirtyEvents[i].Value);
                        }
                        break;
                    case NetworkListEvent<T>.EventType.RemoveAt:
                        {
                            writer.WriteValueSafe(m_DirtyEvents[i].Index);
                        }
                        break;
                    case NetworkListEvent<T>.EventType.Value:
                        {
                            writer.WriteValueSafe(m_DirtyEvents[i].Index);
                            writer.WriteValueSafe(m_DirtyEvents[i].Value);
                        }
                        break;
                    case NetworkListEvent<T>.EventType.Clear:
                        {
                            //Nothing has to be written
                        }
                        break;
                }
            }
        }

        /// <inheritdoc />
        public override void WriteField(ref FastBufferWriter writer)
        {
            writer.WriteValueSafe((ushort)m_List.Length);
            for (int i = 0; i < m_List.Length; i++)
            {
                writer.WriteValueSafe(m_List[i]);
            }
        }

        /// <inheritdoc />
        public override void ReadField(ref FastBufferReader reader)
        {
            m_List.Clear();
            reader.ReadValueSafe(out ushort count);
            for (int i = 0; i < count; i++)
            {
                reader.ReadValueSafe(out T value);
                m_List.Add(value);
            }
        }

        /// <inheritdoc />
        public override void ReadDelta(ref FastBufferReader reader, bool keepDirtyDelta)
        {
            reader.ReadValueSafe(out ushort deltaCount);
            for (int i = 0; i < deltaCount; i++)
            {
                reader.ReadValueSafe(out NetworkListEvent<T>.EventType eventType);
                switch (eventType)
                {
                    case NetworkListEvent<T>.EventType.Add:
                        {
                            reader.ReadValueSafe(out T value);
                            m_List.Add(value);

                            if (OnListChanged != null)
                            {
                                OnListChanged(new NetworkListEvent<T>
                                {
                                    Type = eventType,
                                    Index = m_List.Length - 1,
                                    Value = m_List[m_List.Length - 1]
                                });
                            }

                            if (keepDirtyDelta)
                            {
                                m_DirtyEvents.Add(new NetworkListEvent<T>()
                                {
                                    Type = eventType,
                                    Index = m_List.Length - 1,
                                    Value = m_List[m_List.Length - 1]
                                });
                            }
                        }
                        break;
                    case NetworkListEvent<T>.EventType.Insert:
                        {
                            reader.ReadValueSafe(out int index);
                            reader.ReadValueSafe(out T value);
                            m_List.InsertRangeWithBeginEnd(index, index + 1);
                            m_List[index] = value;

                            if (OnListChanged != null)
                            {
                                OnListChanged(new NetworkListEvent<T>
                                {
                                    Type = eventType,
                                    Index = index,
                                    Value = m_List[index]
                                });
                            }

                            if (keepDirtyDelta)
                            {
                                m_DirtyEvents.Add(new NetworkListEvent<T>()
                                {
                                    Type = eventType,
                                    Index = index,
                                    Value = m_List[index]
                                });
                            }
                        }
                        break;
                    case NetworkListEvent<T>.EventType.Remove:
                        {
                            reader.ReadValueSafe(out T value);
                            int index = m_List.IndexOf(value);
                            if (index == -1)
                            {
                                break;
                            }

                            m_List.RemoveAt(index);

                            if (OnListChanged != null)
                            {
                                OnListChanged(new NetworkListEvent<T>
                                {
                                    Type = eventType,
                                    Index = index,
                                    Value = value
                                });
                            }

                            if (keepDirtyDelta)
                            {
                                m_DirtyEvents.Add(new NetworkListEvent<T>()
                                {
                                    Type = eventType,
                                    Index = index,
                                    Value = value
                                });
                            }
                        }
                        break;
                    case NetworkListEvent<T>.EventType.RemoveAt:
                        {
                            reader.ReadValueSafe(out int index);
                            T value = m_List[index];
                            m_List.RemoveAt(index);

                            if (OnListChanged != null)
                            {
                                OnListChanged(new NetworkListEvent<T>
                                {
                                    Type = eventType,
                                    Index = index,
                                    Value = value
                                });
                            }

                            if (keepDirtyDelta)
                            {
                                m_DirtyEvents.Add(new NetworkListEvent<T>()
                                {
                                    Type = eventType,
                                    Index = index,
                                    Value = value
                                });
                            }
                        }
                        break;
                    case NetworkListEvent<T>.EventType.Value:
                        {
                            reader.ReadValueSafe(out int index);
                            reader.ReadValueSafe(out T value);
                            if (index < m_List.Length)
                            {
                                m_List[index] = value;
                            }

                            if (OnListChanged != null)
                            {
                                OnListChanged(new NetworkListEvent<T>
                                {
                                    Type = eventType,
                                    Index = index,
                                    Value = value
                                });
                            }

                            if (keepDirtyDelta)
                            {
                                m_DirtyEvents.Add(new NetworkListEvent<T>()
                                {
                                    Type = eventType,
                                    Index = index,
                                    Value = value
                                });
                            }
                        }
                        break;
                    case NetworkListEvent<T>.EventType.Clear:
                        {
                            //Read nothing
                            m_List.Clear();

                            if (OnListChanged != null)
                            {
                                OnListChanged(new NetworkListEvent<T>
                                {
                                    Type = eventType,
                                });
                            }

                            if (keepDirtyDelta)
                            {
                                m_DirtyEvents.Add(new NetworkListEvent<T>()
                                {
                                    Type = eventType
                                });
                            }
                        }
                        break;
                    case NetworkListEvent<T>.EventType.Full:
                        {
                            ReadField(ref reader);
                            ResetDirty();
                        }
                        break;
                }
            }
        }

        /// <inheritdoc />
        public IEnumerator<T> GetEnumerator()
        {
            return m_List.GetEnumerator();
        }

        /// <inheritdoc />
        public void Add(T item)
        {
            m_List.Add(item);

            var listEvent = new NetworkListEvent<T>()
            {
                Type = NetworkListEvent<T>.EventType.Add,
                Value = item,
                Index = m_List.Length - 1
            };

            HandleAddListEvent(listEvent);
        }

        /// <inheritdoc />
        public void Clear()
        {
            m_List.Clear();

            var listEvent = new NetworkListEvent<T>()
            {
                Type = NetworkListEvent<T>.EventType.Clear
            };

            HandleAddListEvent(listEvent);
        }

        /// <inheritdoc />
        public bool Contains(T item)
        {
            int index = NativeArrayExtensions.IndexOf(m_List, item);
            return index == -1;
        }

        /// <inheritdoc />
        public bool Remove(T item)
        {
            int index = NativeArrayExtensions.IndexOf(m_List, item);
            if (index == -1)
            {
                return false;
            }

            m_List.RemoveAt(index);
            var listEvent = new NetworkListEvent<T>()
            {
                Type = NetworkListEvent<T>.EventType.Remove,
                Value = item
            };

            HandleAddListEvent(listEvent);
            return true;
        }

        /// <inheritdoc />
        public int Count => m_List.Length;

        /// <inheritdoc />
        public int IndexOf(T item)
        {
            return m_List.IndexOf(item);
        }

        /// <inheritdoc />
        public void Insert(int index, T item)
        {
            m_List.InsertRangeWithBeginEnd(index, index + 1);
            m_List[index] = item;

            var listEvent = new NetworkListEvent<T>()
            {
                Type = NetworkListEvent<T>.EventType.Insert,
                Index = index,
                Value = item
            };

            HandleAddListEvent(listEvent);
        }

        /// <inheritdoc />
        public void RemoveAt(int index)
        {
            m_List.RemoveAt(index);

            var listEvent = new NetworkListEvent<T>()
            {
                Type = NetworkListEvent<T>.EventType.RemoveAt,
                Index = index
            };

            HandleAddListEvent(listEvent);
        }

        /// <inheritdoc />
        public T this[int index]
        {
            get => m_List[index];
            set
            {
                m_List[index] = value;

                var listEvent = new NetworkListEvent<T>()
                {
                    Type = NetworkListEvent<T>.EventType.Value,
                    Index = index,
                    Value = value
                };

                HandleAddListEvent(listEvent);
            }
        }

        private void HandleAddListEvent(NetworkListEvent<T> listEvent)
        {
            m_DirtyEvents.Add(listEvent);
            OnListChanged?.Invoke(listEvent);
        }

        public int LastModifiedTick
        {
            get
            {
                // todo: implement proper network tick for NetworkList
                return NetworkTickSystem.NoTick;
            }
        }

        public override void Dispose()
        {
            m_List.Dispose();
            m_DirtyEvents.Dispose();
        }
    }

    /// <summary>
    /// Struct containing event information about changes to a NetworkList.
    /// </summary>
    /// <typeparam name="T">The type for the list that the event is about</typeparam>
    public struct NetworkListEvent<T>
    {
        /// <summary>
        /// Enum representing the different operations available for triggering an event.
        /// </summary>
        public enum EventType : byte
        {
            /// <summary>
            /// Add
            /// </summary>
            Add,

            /// <summary>
            /// Insert
            /// </summary>
            Insert,

            /// <summary>
            /// Remove
            /// </summary>
            Remove,

            /// <summary>
            /// Remove at
            /// </summary>
            RemoveAt,

            /// <summary>
            /// Value changed
            /// </summary>
            Value,

            /// <summary>
            /// Clear
            /// </summary>
            Clear,

            /// <summary>
            /// Full list refresh
            /// </summary>
            Full
        }

        /// <summary>
        /// Enum representing the operation made to the list.
        /// </summary>
        public EventType Type;

        /// <summary>
        /// The value changed, added or removed if available.
        /// </summary>
        public T Value;

        /// <summary>
        /// the index changed, added or removed if available
        /// </summary>
        public int Index;
    }
}
