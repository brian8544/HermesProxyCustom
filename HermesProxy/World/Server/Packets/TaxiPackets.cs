/*
 * Copyright (C) 2012-2020 CypherCore <http://github.com/CypherCore>
 * 
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */


using Framework;
using Framework.Constants;
using Framework.GameMath;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using System.Collections.Generic;

namespace HermesProxy.World.Server.Packets
{
    class TaxiNodeStatusPkt : ServerPacket
    {
        public TaxiNodeStatusPkt() : base(Opcode.SMSG_TAXI_NODE_STATUS) { }

        public override void Write()
        {
            if (Settings.ClientBuild == ClientVersionBuild.V3_3_5a_12340)
            {
                // Wrath uses the pre-Cata taxi node status shape: uint64 guid + uint8 status.
                // The packed-guid/bitfield layout below belongs to newer clients and can
                // corrupt the next taxi/gossip packet in a 3.3.5a frontend.
                _worldPacket.WriteGuid(FlightMaster.To64());
                _worldPacket.WriteUInt8((byte)Status);
                return;
            }

            _worldPacket.WritePackedGuid128(FlightMaster);
            _worldPacket.WriteBits(Status, 2);
            _worldPacket.FlushBits();
        }

        public WowGuid128 FlightMaster;
        public TaxiNodeStatus Status;
    }

    public class ShowTaxiNodes : ServerPacket
    {
        // WotLK's taxi mask is a fixed 12 x uint32 array (48 bytes).  Vanilla sends
        // fewer mask bytes, so we zero-extend the legacy mask instead of using the
        // newer variable-count/packed-guid layout.
        private const int WotlkTaxiMaskBytes = 12 * 4;

        public ShowTaxiNodes() : base(Opcode.SMSG_SHOW_TAXI_NODES) { }

        public override void Write()
        {
            if (Settings.ClientBuild == ClientVersionBuild.V3_3_5a_12340)
            {
                WriteWotlk335();
                return;
            }

            _worldPacket.WriteBit(WindowInfo != null);
            _worldPacket.FlushBits();

            List<byte> canLandNodes = new List<byte>(CanLandNodes);
            CleanupNodes(canLandNodes);
            _worldPacket.WriteInt32(canLandNodes.Count);
            List<byte> canUseNodes = new List<byte>(CanUseNodes);
            CleanupNodes(canUseNodes);
            _worldPacket.WriteInt32(canUseNodes.Count);

            if (WindowInfo != null)
            {
                _worldPacket.WritePackedGuid128(WindowInfo.UnitGUID);
                _worldPacket.WriteUInt32(WindowInfo.CurrentNode);
            }
            
            foreach (var node in canLandNodes)
                _worldPacket.WriteUInt8(node);
            
            foreach (var node in canUseNodes)
                _worldPacket.WriteUInt8(node);
        }

        private void WriteWotlk335()
        {
            _worldPacket.WriteUInt32(WindowInfo != null ? 1u : 0u);
            if (WindowInfo != null)
            {
                _worldPacket.WriteGuid(WindowInfo.UnitGUID.To64());
                _worldPacket.WriteUInt32(WindowInfo.CurrentNode);
            }

            byte[] mask = BuildWotlkTaxiMask();
            for (int i = 0; i < mask.Length; i += 4)
            {
                uint word = (uint)(mask[i] | (mask[i + 1] << 8) | (mask[i + 2] << 16) | (mask[i + 3] << 24));
                _worldPacket.WriteUInt32(word);
            }
        }

        private byte[] BuildWotlkTaxiMask()
        {
            byte[] mask = new byte[WotlkTaxiMaskBytes];

            // For 3.3.5a there is one known/available-node mask.  Merge both lists so
            // a node visible in either legacy mask remains selectable instead of opening
            // a half-empty taxi map.
            for (int i = 0; i < mask.Length; i++)
            {
                byte value = 0;
                if (i < CanLandNodes.Count)
                    value |= CanLandNodes[i];
                if (i < CanUseNodes.Count)
                    value |= CanUseNodes[i];
                mask[i] = value;
            }

            return mask;
        }

        // remove extra zeroes after last node
        private void CleanupNodes(List<byte> nodes)
        {
            int lastIndex = -1;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i] != 0)
                    lastIndex = i;
            }

            if ((lastIndex + 1) == nodes.Count)
                return;

            if (lastIndex == -1)
            {
                nodes.Clear();
                return;
            }

            nodes.RemoveRange(lastIndex + 1, nodes.Count - (lastIndex + 1));
        }

        public ShowTaxiNodesWindowInfo WindowInfo;
        public List<byte> CanLandNodes = new(); // Nodes known by player
        public List<byte> CanUseNodes = new(); // Nodes available for use - this can temporarily disable a known node
    }

    public class ShowTaxiNodesWindowInfo
    {
        public WowGuid128 UnitGUID;
        public uint CurrentNode;
    }

    class ActivateTaxi : ClientPacket
    {
        public ActivateTaxi(WorldPacket packet) : base(packet) { }

        public override void Read()
        {
            FlightMaster = _worldPacket.ReadPackedGuid128();
            Node = _worldPacket.ReadUInt32();
            GroundMountID = _worldPacket.ReadUInt32();
            FlyingMountID = _worldPacket.ReadUInt32();
        }

        public WowGuid128 FlightMaster;
        public uint Node;
        public uint GroundMountID;
        public uint FlyingMountID;
    }

    class NewTaxiPath : ServerPacket
    {
        public NewTaxiPath() : base(Opcode.SMSG_NEW_TAXI_PATH) { }

        public override void Write() { }
    }

    class ActivateTaxiReplyPkt : ServerPacket
    {
        public ActivateTaxiReplyPkt() : base(Opcode.SMSG_ACTIVATE_TAXI_REPLY) { }

        public override void Write()
        {
            _worldPacket.WriteBits(Reply, 4);
            _worldPacket.FlushBits();
        }

        public ActivateTaxiReply Reply;
    }
}
