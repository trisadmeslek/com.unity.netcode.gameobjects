using System;
using System.Collections.Generic;
using System.IO;
using MLAPI.Configuration;
using MLAPI.Messaging;
using MLAPI.NetworkVariable;
using MLAPI.Serialization;
using MLAPI.Serialization.Pooled;
using MLAPI.Transports;
using UnityEngine;
using UnityEngine.UIElements;

namespace MLAPI
{
    internal struct Key
    {
        public ulong m_NetworkObjectId; // the NetworkObjectId of the owning GameObject
        public ushort m_BehaviourIndex; // the index of the behaviour in this GameObject
        public ushort m_VariableIndex; // the index of the variable in this NetworkBehaviour
    }
    internal struct Entry
    {
        public Key key;
        public ushort m_TickWritten; // the network tick at which this variable was set
        public ushort m_Position; // the offset in our m_Buffer
        public ushort m_Length; // the length of the data in m_Buffer

        public const int k_NotFound = -1;
    }

    internal class EntryBlock
    {
        private const int k_MaxVariables = 64;

        public Entry[] m_Entries = new Entry[k_MaxVariables];
        public int m_LastEntry = 0;

        public int Find(Key key)
        {
            for (int i = 0; i < m_LastEntry; i++)
            {
                if (m_Entries[i].key.m_NetworkObjectId == key.m_NetworkObjectId &&
                    m_Entries[i].key.m_BehaviourIndex == key.m_BehaviourIndex &&
                    m_Entries[i].key.m_VariableIndex == key.m_VariableIndex)
                {
                    return i;
                }
            }

            return Entry.k_NotFound;
        }

        public int AddEntry(ulong networkObjectId, int behaviourIndex, int variableIndex)
        {
            var pos = m_LastEntry++;
            var entry = m_Entries[pos];

            entry.key.m_NetworkObjectId = networkObjectId;
            entry.key.m_BehaviourIndex = (ushort)behaviourIndex;
            entry.key.m_VariableIndex = (ushort)variableIndex;
            entry.m_TickWritten = 0;
            entry.m_Position = 0;
            entry.m_Length = 0;
            m_Entries[pos] = entry;

            return pos;
        }

    }

    public class SnapshotSystem : INetworkUpdateSystem, IDisposable
    {
        private EntryBlock m_Snapshot = new EntryBlock();
        private EntryBlock m_ReceivedSnapshot = new EntryBlock();

        // todo: split this buffer into parts per EntryBlock. Move the beg and end marker into an associated class, too
        byte[] m_Buffer = new byte[20000];
        private int m_Beg = 0;
        private int m_End = 0;

        public SnapshotSystem()
        {
            this.RegisterNetworkUpdate(NetworkUpdateStage.EarlyUpdate);
        }

        public void Dispose()
        {
            this.UnregisterNetworkUpdate(NetworkUpdateStage.EarlyUpdate);
        }

        public void NetworkUpdate(NetworkUpdateStage updateStage)
        {
            switch (updateStage)
            {
                case NetworkUpdateStage.EarlyUpdate:

                    // todo: ConnectedClientsList is only valid on the host
                    for (int i = 0; i < NetworkManager.Singleton.ConnectedClientsList.Count; i++)
                    {
                        var clientId = NetworkManager.Singleton.ConnectedClientsList[i].ClientId;


                        // Send the entry index and the buffer where the variables are serialized
                        var buffer = PooledNetworkBuffer.Get();

                        WriteIndex(buffer);
                        WriteBuffer(buffer);

                        NetworkManager.Singleton.MessageSender.Send(clientId, NetworkConstants.SNAPSHOT_DATA, NetworkChannel.SnapshotExchange, buffer);
                        buffer.Dispose();
                    }

                    DebugDisplayStore(m_Snapshot.m_Entries, m_Snapshot.m_LastEntry, "Entries");
                    DebugDisplayStore(m_Snapshot.m_Entries, m_Snapshot.m_LastEntry, "Received Entries");
                    break;
            }
        }

        private void WriteIndex(NetworkBuffer buffer)
        {
            using (var writer = PooledNetworkWriter.Get(buffer))
            {
                writer.WriteInt16((short)m_Snapshot.m_LastEntry);

                for (var i = 0; i < m_Snapshot.m_LastEntry; i++)
                {
                    writer.WriteUInt64(m_Snapshot.m_Entries[i].key.m_NetworkObjectId);
                    writer.WriteUInt16(m_Snapshot.m_Entries[i].key.m_BehaviourIndex);
                    writer.WriteUInt16(m_Snapshot.m_Entries[i].key.m_VariableIndex);
                    writer.WriteUInt16(m_Snapshot.m_Entries[i].m_TickWritten);
                    writer.WriteUInt16(m_Snapshot.m_Entries[i].m_Position);
                    writer.WriteUInt16(m_Snapshot.m_Entries[i].m_Length);
                }
            }
        }

        private void WriteBuffer(NetworkBuffer buffer)
        {
            // todo: this sends the whole buffer
            // we'll need to build a per-client list
            buffer.Write(m_Buffer, 0, m_End);
        }

        private void AllocateEntry(ref Entry entry, long size)
        {
            // todo: deal with free space
            // todo: deal with full buffer

            entry.m_Position = (ushort)m_Beg;
            entry.m_Length = (ushort)size;
            m_Beg += (int)size;
        }

        public void Store(ulong networkObjectId, int behaviourIndex, int variableIndex, INetworkVariable networkVariable)
        {
            Key k;
            k.m_NetworkObjectId = networkObjectId;
            k.m_BehaviourIndex = (ushort)behaviourIndex;
            k.m_VariableIndex = (ushort)variableIndex;

            int pos = m_Snapshot.Find(k);
            if (pos == Entry.k_NotFound)
            {
                pos = m_Snapshot.AddEntry(networkObjectId, behaviourIndex, variableIndex);
            }

            // write var into buffer, possibly adjusting entry's position and length
            using (var varBuffer = PooledNetworkBuffer.Get())
            {
                networkVariable.WriteDelta(varBuffer);
                if (varBuffer.Length > m_Snapshot.m_Entries[pos].m_Length)
                {
                    // allocate this Entry's buffer
                    AllocateEntry(ref m_Snapshot.m_Entries[pos], varBuffer.Length);
                }

                m_Snapshot.m_Entries[pos].m_TickWritten = NetworkManager.Singleton.NetworkTickSystem.GetTick(); // todo: from here
                // Copy the serialized NetworkVariable into our buffer
                Buffer.BlockCopy(varBuffer.GetBuffer(), 0, m_Buffer, m_Snapshot.m_Entries[pos].m_Position, (int)varBuffer.Length);
            }
        }

        public void ReadSnapshot(Stream snapshotStream)
        {
            // todo: this is sub-optimal, as it allocates. Review
            List<int> entriesPositionToRead = new List<int>();


            using (var reader = PooledNetworkReader.Get(snapshotStream))
            {
                Entry entry;
                short entries = reader.ReadInt16();
                Debug.Log(string.Format("Got {0} entries", entries));

                for (var i = 0; i < entries; i++)
                {
                    entry.key.m_NetworkObjectId = reader.ReadUInt64();
                    entry.key.m_BehaviourIndex = reader.ReadUInt16();
                    entry.key.m_VariableIndex = reader.ReadUInt16();
                    entry.m_TickWritten = reader.ReadUInt16();
                    entry.m_Position = reader.ReadUInt16();
                    entry.m_Length = reader.ReadUInt16();

                    int pos = m_ReceivedSnapshot.Find(entry.key);
                    if (pos == Entry.k_NotFound)
                    {
                        pos = m_ReceivedSnapshot.AddEntry(entry.key.m_NetworkObjectId, entry.key.m_BehaviourIndex, entry.key.m_VariableIndex);
                    }

                    if (m_ReceivedSnapshot.m_Entries[pos].m_Length < entry.m_Length)
                    {
                        AllocateEntry(ref entry, entry.m_Length);
                    }
                    m_ReceivedSnapshot.m_Entries[pos] = entry;

                    entriesPositionToRead.Add(pos);
                }
            }

            foreach (var pos in entriesPositionToRead)
            {
                if (m_ReceivedSnapshot.m_Entries[pos].m_TickWritten > 0)
                {

                    snapshotStream.Read(m_Buffer, m_ReceivedSnapshot.m_Entries[pos].m_Position,
                        m_ReceivedSnapshot.m_Entries[pos].m_Length);

                    var spawnedObjects = NetworkManager.Singleton.SpawnManager.SpawnedObjects;

                    if (spawnedObjects.ContainsKey(m_ReceivedSnapshot.m_Entries[pos].key.m_NetworkObjectId))
                    {
                        var behaviour = spawnedObjects[m_ReceivedSnapshot.m_Entries[pos].key.m_NetworkObjectId]
                            .GetNetworkBehaviourAtOrderIndex(m_ReceivedSnapshot.m_Entries[pos].key.m_BehaviourIndex);
                        var nv = behaviour.NetworkVariableFields[m_ReceivedSnapshot.m_Entries[pos].key.m_VariableIndex];

                        MemoryStream stream = new MemoryStream(m_Buffer, m_ReceivedSnapshot.m_Entries[pos].m_Position,
                            m_ReceivedSnapshot.m_Entries[pos].m_Length);
                        nv.ReadDelta(stream, false, 0, 0);
                    }
                }
            }
        }

        private void DebugDisplayStore(Entry[] entries, int entryLength, string name)
        {
            string table = "=== Snapshot table === " + name + " ===\n";
            for (int i = 0; i < entryLength; i++)
            {
                table += string.Format("NetworkObject {0}:{1} range [{2}, {3}]\n", entries[i].key.m_NetworkObjectId,
                    entries[i].key.m_VariableIndex, entries[i].m_Position, entries[i].m_Position + entries[i].m_Length);
            }
            Debug.Log(table);
        }
    }
}
