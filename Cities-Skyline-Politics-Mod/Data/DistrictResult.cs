using System;
using System.Collections.Generic;
using ColossalFramework.IO;
using UnityEngine;

namespace PoliticsMod
{
    /// <summary>
    /// Per-district election result record.
    /// Captures voter turnout, party shares, senate winner, and demographic splits
    /// for a single administrative district (or the unzoned outer city).
    /// </summary>
    public class DistrictResult : IDataContainer
    {
        public byte DistrictId;
        public string DistrictName;
        public int TotalVotes;

        // Plurality winner for the single-member Senate seat in this district.
        // -1 if none (e.g. unzoned outer area or zero votes).
        public int SenateWinnerParty = -1;

        // Votes by party (length = PartyCount)
        public int[] VotesByParty;
        public float[] VoteShareByParty;

        // Grievance breakdown (9 concrete values matching Grievance enum)
        public int[] VotesByGrievance;

        // Demographic cross-tabs
        // Age: 0=Young, 1=Adult, 2=Senior (3 x PartyCount)
        public int[,] VotesByAgeParty;
        // Education: 0=Uneducated, 1=Educated, 2=WellEducated, 3=HighlyEducated (4 x PartyCount)
        public int[,] VotesByEduParty;
        // Wealth: 0=Low, 1=Medium, 2=High (3 x PartyCount)
        public int[,] VotesByWealthParty;

        public void Serialize(DataSerializer s)
        {
            s.WriteInt32(DistrictId);
            s.WriteSharedString(DistrictName ?? string.Empty);
            s.WriteInt32(TotalVotes);
            s.WriteInt32(SenateWinnerParty);

            int pLen = VotesByParty != null ? VotesByParty.Length : 0;
            s.WriteInt32(pLen);
            for (int i = 0; i < pLen; i++) s.WriteInt32(VotesByParty[i]);

            int sLen = VoteShareByParty != null ? VoteShareByParty.Length : 0;
            s.WriteInt32(sLen);
            for (int i = 0; i < sLen; i++) s.WriteFloat(VoteShareByParty[i]);

            int gLen = VotesByGrievance != null ? VotesByGrievance.Length : 0;
            s.WriteInt32(gLen);
            for (int i = 0; i < gLen; i++) s.WriteInt32(VotesByGrievance[i]);

            ElectionResult.WriteMatrix(s, VotesByAgeParty, 3);
            ElectionResult.WriteMatrix(s, VotesByEduParty, 4);
            ElectionResult.WriteMatrix(s, VotesByWealthParty, 3);
        }

        public void Deserialize(DataSerializer s)
        {
            DistrictId = (byte)s.ReadInt32();
            DistrictName = s.ReadSharedString();
            TotalVotes = s.ReadInt32();
            SenateWinnerParty = s.ReadInt32();

            int pLen = s.ReadInt32();
            VotesByParty = new int[pLen];
            for (int i = 0; i < pLen; i++) VotesByParty[i] = s.ReadInt32();

            int sLen = s.ReadInt32();
            VoteShareByParty = new float[sLen];
            for (int i = 0; i < sLen; i++) VoteShareByParty[i] = s.ReadFloat();

            int gLen = s.ReadInt32();
            VotesByGrievance = new int[gLen];
            for (int i = 0; i < gLen; i++) VotesByGrievance[i] = s.ReadInt32();

            VotesByAgeParty = ElectionResult.ReadMatrix(s);
            VotesByEduParty = ElectionResult.ReadMatrix(s);
            VotesByWealthParty = ElectionResult.ReadMatrix(s);
        }

        public void AfterDeserialize(DataSerializer s) { }
    }
}

