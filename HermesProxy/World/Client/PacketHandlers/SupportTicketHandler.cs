using Framework.Constants;
using Framework.IO;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Server.Packets;

namespace HermesProxy.World.Client
{
    public partial class WorldClient
    {
        [PacketHandler(Opcode.SMSG_GM_TICKET_CREATE)]
        void HandleGmTicketCreate(WorldPacket packet)
        {
            var response = (LegacyGmTicketResponse) packet.ReadUInt32();
            if (IsWotlkFrontendClient())
                TryForwardLegacyPayloadToWotlkClient(packet);

            bool isError = !(response is LegacyGmTicketResponse.CreateSuccess or LegacyGmTicketResponse.UpdateSuccess);
            Session.SendHermesTextMessage($"GM Ticket Status: {response}", isError);
        }

        [PacketHandler(Opcode.SMSG_GM_TICKET_SYSTEM_STATUS)]
        void HandleGmTicketSystemStatus(WorldPacket packet)
        {
            if (IsWotlkFrontendClient())
            {
                TryForwardLegacyPayloadToWotlkClient(packet);
                return;
            }

            while (packet.CanRead())
                packet.ReadUInt8();
        }

        [PacketHandler(Opcode.SMSG_GM_TICKET_GET_TICKET)]
        void HandleGmTicketGetTicket(WorldPacket packet)
        {
            if (!IsWotlkFrontendClient())
            {
                TryForwardLegacyPayloadToWotlkClient(packet);
                return;
            }

            uint status = packet.ReadUInt32();
            ByteBuffer payload = new();
            payload.WriteUInt32(status);

            if (status == 0x06 && packet.CanRead())
            {
                string text = packet.ReadCString();
                if (packet.CanRead())
                    packet.ReadUInt8(); // legacy ticket category

                float ticketAge = packet.CanRead() ? packet.ReadFloat() : 0f;
                float oldestTicketAge = packet.CanRead() ? packet.ReadFloat() : 0f;
                float oldestTicketUpdateAge = packet.CanRead() ? packet.ReadFloat() : 0f;
                byte escalatedStatus = packet.CanRead() ? packet.ReadUInt8() : (byte)0;
                byte openedByGm = packet.CanRead() ? packet.ReadUInt8() : (byte)0;

                payload.WriteUInt32(0); // legacy backends do not expose a client-visible ticket id here
                payload.WriteCString(text);
                payload.WriteUInt8(0); // need more help
                payload.WriteFloat(ticketAge);
                payload.WriteFloat(oldestTicketAge);
                payload.WriteFloat(oldestTicketUpdateAge);
                payload.WriteUInt8(escalatedStatus);
                payload.WriteUInt8(openedByGm);
            }

            SendPacketToClient(new RawServerPacket(Opcode.SMSG_GM_TICKET_GET_TICKET, ConnectionType.Instance, payload.GetData()));
        }
    }
}
