using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using ColossalFramework;
using ColossalFramework.IO;
using ColossalFramework.Math;
using ColossalFramework.UI;
using HarmonyLib;
using ICities;
using UnityEngine;
using PoliticsMod.Localization;

namespace PoliticsMod
{


    // ========================================================================
    //  ELECTION ENGINE - voter simulation, coalition formation, policy apply.
    // ========================================================================
    public static class ElectionEngine
    {
        private static System.Random _rng = new System.Random();
        // Expose the shared RNG so auxiliary samplers (opinion polling) use
        // the same random stream as the election engine.
        public static System.Random Rng { get { return _rng; } }
        // Temp storage for the last full-sample's per-grievance vote counts.
        // Picked up by RunElection and stored on the ElectionResult.
        private static int[] _lastGrievanceTally;
        private static int[,] _lastAgeTally, _lastEduTally, _lastWealthTally;
        private static List<DistrictResult> _lastDistrictResults;

        // ---- Deficit pressure (right-wing nudge when city is losing money) ----
        // Updated by PoliticsThreading once per in-game week.
        public static int DeficitWeeks;          // consecutive weeks in deficit
        public static long LastCashSeen = long.MinValue; // sentinel = uninitialized
        public static float DaysSinceLastDeficitChirp = 0f;

        /// <summary>
        /// Current deficit pressure on voters' economic axis, 0..0.35.
        /// Kicks in at 1 week of deficit, saturates around 6 weeks.
        /// Scaled by RuntimeConfig.DeficitPressureMultiplier (1.0 = default).
        /// </summary>
        public static float DeficitPressure
        {
            get
            {
                if (DeficitWeeks <= 0) return 0f;
                float t = Mathf.Clamp01(DeficitWeeks / 6f);
                return (0.05f + 0.30f * t) * RuntimeConfig.DeficitPressureMultiplier;
            }
        }

        public static void TriggerCampaign(bool force)
        {
            try
            {
                var st = PoliticsState.Instance;
                if (st == null) return;
                st.Phase = ElectionPhase.Campaign;
                st.DaysSinceCampaignStart = 0f;
                // Seed support from previous result, or uniform if no prior election.
                if (st.CurrentSupport == null || st.CurrentSupport.Length != PartyCountRef.Value)
                    st.CurrentSupport = new float[PartyCountRef.Value];

                if (st.LastResult != null && st.LastResult.VoteShareByParty != null)
                {
                    int lim = Math.Min(st.CurrentSupport.Length, st.LastResult.VoteShareByParty.Length);
                    for (int i = 0; i < lim; i++)
                        st.CurrentSupport[i] = st.LastResult.VoteShareByParty[i];
                    for (int i = lim; i < st.CurrentSupport.Length; i++)
                        st.CurrentSupport[i] = 1f / Math.Max(1, st.CurrentSupport.Length);
                }
                else
                {
                    float u = 1f / Math.Max(1, st.CurrentSupport.Length);
                    for (int i = 0; i < st.CurrentSupport.Length; i++) st.CurrentSupport[i] = u;
                }
                int days = (int)RuntimeConfig.CampaignLengthDays;
                bool isKo = L10n.CurrentCode.StartsWith("ko");
                string toastMsg = force
                    ? (isKo ? string.Format("조기 총선 공고! {0}일간 선거 운동이 진행됩니다.", days) : "Snap election called! Campaign runs for " + days + " days.")
                    : (isKo ? string.Format("선거철 돌입 - {0}일 후 총선이 실시됩니다.", days) : "Campaign begins - elections in " + days + " days");
                ShowToast(toastMsg);
                // Chirper announcement from a generic "City News" account (senderID 0u = system/news)
                string newsSender = L10n.T(L10nKeys.Chirp_CityNews_Sender);
                string newsMsg = L10n.T(L10nKeys.Chirp_Election_Called, days);
                PostChirp(newsSender, newsMsg, 0u);
                // Each party chirps a slogan (suppressed in MinimalChirps mode)
                if (!DebugFlags.MinimalChirps && Config.Parties != null)
                {
                    for (int i = 0; i < Config.Parties.Length; i++)
                    {
                        var p = Config.Parties[i];
                        if (p == null) continue;
                        string slogan = PickSloganForParty(i, "campaign");
                        PostChirp(p.FullName, slogan, 0u);
                    }
                }
            }
            catch (Exception ex)
            {
                PoliticsUserMod.Log("TriggerCampaign caught exception: " + ex);
            }
        }

        /// <summary>
        /// Pick a slogan body for a party's chirp, bucketed by the party's
        /// ideology (economic x-axis, social y-axis) and context
        /// ("campaign" / "victory" / "defeat"). The party's ShortName is
        /// appended as a hashtag at the very end, so user-renamed parties
        /// automatically get the right tag without editing slogan strings.
        ///
        /// Parties that are near-neutral on BOTH the economic and social
        /// axes don't fit any flavor pool and just post a bare hashtag.
        /// </summary>
        private static string PickSloganForParty(int partyId, string context)
        {
            if (Config.Parties == null || partyId < 0 || partyId >= Config.Parties.Length) return "";
            var p = Config.Parties[partyId];
            if (p == null) return "";
            bool isKo = L10n.CurrentCode.StartsWith("ko");
            string[] pool = SelectSloganPool(p.Ideology, context, isKo);

            // No ideological fit (or empty pool) -> just the hashtag.
            if (pool == null || pool.Length == 0)
                return "#" + p.ShortName + (isKo ? " #투표" : " #Vote");

            string body = pool[UnityEngine.Random.Range(0, pool.Length)];
            return body + " #" + p.ShortName;
        }

        /// <summary>
        /// Bucket the party's ideology into one of six cells (3 economic
        /// x 2 social) and return the matching slogan pool for the context.
        /// Returns null when the party is too centrist on BOTH axes to pick
        /// a flavor pool, so the caller can fall back to a bare hashtag.
        /// </summary>
        private static string[] SelectSloganPool(Vector3 ideology, string context, bool isKo)
        {
            float econ = ideology.x;
            float social = ideology.y;

            // "Truly neutral" fallback: inside the dead zone on both axes we
            // have nothing interesting to say, so don't pretend to.
            if (Mathf.Abs(econ) < 0.15f && Mathf.Abs(social) < 0.15f) return null;

            // econ: 0 = left (<-0.3), 1 = center, 2 = right (>+0.3)
            int econBucket = econ < -0.3f ? 0 : (econ > +0.3f ? 2 : 1);
            // social: 0 = progressive (y<0), 1 = traditional (y>=0)
            int socialBucket = social < 0f ? 0 : 1;

            string[][][] matrix;
            if (isKo)
            {
                if (context == "campaign") matrix = KoCampaignPools;
                else if (context == "victory") matrix = KoVictoryPools;
                else matrix = KoDefeatPools;
            }
            else
            {
                if (context == "campaign") matrix = CampaignPools;
                else if (context == "victory") matrix = VictoryPools;
                else matrix = DefeatPools;
            }

            if (matrix == null || econBucket < 0 || econBucket >= matrix.Length) return null;
            if (matrix[econBucket] == null || socialBucket < 0 || socialBucket >= matrix[econBucket].Length) return null;
            return matrix[econBucket][socialBucket];
        }

        private static readonly string[][][] KoCampaignPools = new string[][][]
        {
            // --- LEFT ------------------------------------------------------
            new string[][]
            {
                // left + progressive
                new string[]
                {
                    "모두를 위한 더 푸르고 정의로운 도시!",
                    "주거와 복지는 시민의 기본 권리입니다.",
                },
                // left + traditional
                new string[]
                {
                    "노동이 존중받는 도시, 튼튼한 우리 동네.",
                    "일하는 시민이 일군 도시, 시민의 품으로!",
                },
            },
            // --- CENTER ----------------------------------------------------
            new string[][]
            {
                // center + progressive
                new string[]
                {
                    "스마트하고 실용적인 미래 도시 개혁.",
                    "데이터와 근거에 기반한 합리적 시정.",
                },
                // center + traditional
                new string[]
                {
                    "안정된 시정, 믿음직한 상식.",
                    "도시를 지켜온 유능함으로 미래를 엽니다.",
                },
            },
            // --- RIGHT -----------------------------------------------------
            new string[][]
            {
                // right + progressive
                new string[]
                {
                    "세금 감면, 규제 혁신, 자유로운 기회의 도시!",
                    "시장의 자율이 번영하는 도시를 만듭니다.",
                },
                // right + traditional
                new string[]
                {
                    "원칙과 법치, 알뜰하고 튼튼한 시정.",
                    "원칙으로 바로 세우는 위대한 도시!",
                },
            },
        };

        private static readonly string[][][] KoVictoryPools = new string[][][]
        {
            // --- LEFT ------------------------------------------------------
            new string[][]
            {
                // left + progressive
                new string[]
                {
                    "정의와 환경을 바라는 시민들의 위대한 승리!",
                    "함께 꿈꾸던 살기 좋은 도시를 이제 만듭니다.",
                },
                // left + traditional
                new string[]
                {
                    "묵묵히 땀 흘려 일하는 모든 시민의 승리!",
                    "연대와 상식이 마침내 승리했습니다.",
                },
            },
            // --- CENTER ----------------------------------------------------
            new string[][]
            {
                // center + progressive
                new string[]
                {
                    "유능하고 따뜻한 시정을 이끌라는 시민의 명령!",
                    "상식과 개혁의 새로운 시대가 열립니다.",
                },
                // center + traditional
                new string[]
                {
                    "안정과 균형을 선택해 주신 시민들께 감사드립니다.",
                    "모든 시민을 하나로 아우르는 시정을 펼치겠습니다.",
                },
            },
            // --- RIGHT -----------------------------------------------------
            new string[][]
            {
                // right + progressive
                new string[]
                {
                    "자유와 번영을 향한 위대한 시민의 선택!",
                    "역동적인 도시의 미래가 지금 시작됩니다.",
                },
                // right + traditional
                new string[]
                {
                    "원칙과 가치를 회복하라는 시민의 준엄한 명령!",
                    "침묵하던 다수의 시민들이 행동으로 응답했습니다.",
                },
            },
        };

        private static readonly string[][][] KoDefeatPools = new string[][][]
        {
            // --- LEFT ------------------------------------------------------
            new string[][]
            {
                // left + progressive
                new string[]
                {
                    "더 나은 도시를 향한 우리의 발걸음은 멈추지 않습니다.",
                    "더 낮은 곳에서 시민들과 끝까지 함께하겠습니다.",
                },
                // left + traditional
                new string[]
                {
                    "일하는 시민들을 위한 우리의 연대는 계속됩니다.",
                    "다시 힘을 모아 더 굳건하게 돌아오겠습니다.",
                },
            },
            // --- CENTER ----------------------------------------------------
            new string[][]
            {
                // center + progressive
                new string[]
                {
                    "시민의 선택을 겸허히 수용하며 내일을 준비합니다.",
                    "합리적인 정책은 결코 사라지지 않습니다.",
                },
                // center + traditional
                new string[]
                {
                    "선거 결과를 존중하며 책임 있는 야당이 되겠습니다.",
                    "시민 여러분의 목소리에 더욱 깊이 귀 기울이겠습니다.",
                },
            },
            // --- RIGHT -----------------------------------------------------
            new string[][]
            {
                // right + progressive
                new string[]
                {
                    "자유를 향한 여정은 계속됩니다. 흔들리지 않겠습니다.",
                    "시장의 원칙은 다시 증명될 것입니다.",
                },
                // right + traditional
                new string[]
                {
                    "도시의 근본 가치를 지키기 위해 끝까지 함께하겠습니다.",
                    "결과를 겸허히 받아들이며 원칙을 지켜가겠습니다.",
                },
            },
        };

        // --------------------------------------------------------------
        // Slogan matrix: [econBucket][socialBucket][variantIndex].
        //   econBucket:   0 = left,         1 = center,       2 = right
        //   socialBucket: 0 = progressive,  1 = traditional
        //
        // Each cell has 2 variants, picked randomly. The party's ShortName
        // is appended by PickSloganForParty() so these strings never embed
        // a hashtag.
        // --------------------------------------------------------------
        private static readonly string[][][] CampaignPools = new string[][][]
        {
            // --- LEFT ------------------------------------------------------
            new string[][]
            {
                // left + progressive
                new string[]
                {
                    "A greener, fairer city for all.",
                    "Healthcare and housing are human rights.",
                },
                // left + traditional
                new string[]
                {
                    "Good jobs. Strong unions. Proud neighborhoods.",
                    "The workers built this city. Time to take it back.",
                },
            },
            // --- CENTER ----------------------------------------------------
            new string[][]
            {
                // center + progressive
                new string[]
                {
                    "Smart, pragmatic progress.",
                    "Evidence-based policy for a modern city.",
                },
                // center + traditional
                new string[]
                {
                    "Steady hands. Sound judgment.",
                    "Protect what works. Fix what doesn't.",
                },
            },
            // --- RIGHT -----------------------------------------------------
            new string[][]
            {
                // right + progressive
                new string[]
                {
                    "Lower taxes. Open markets. Open minds.",
                    "Free citizens build the best cities.",
                },
                // right + traditional
                new string[]
                {
                    "Law, order, and lower taxes.",
                    "Back to basics. Back to greatness.",
                },
            },
        };

        private static readonly string[][][] VictoryPools = new string[][][]
        {
            // --- LEFT ------------------------------------------------------
            new string[][]
            {
                // left + progressive
                new string[]
                {
                    "A mandate for justice and the planet.",
                    "Together we build the city we deserve.",
                },
                // left + traditional
                new string[]
                {
                    "A win for every family that works for a living.",
                    "Hard work and solidarity carried the day.",
                },
            },
            // --- CENTER ----------------------------------------------------
            new string[][]
            {
                // center + progressive
                new string[]
                {
                    "A mandate for competent, compassionate government.",
                    "Reason and reform - starting today.",
                },
                // center + traditional
                new string[]
                {
                    "A vote for stability and common sense.",
                    "We will govern for every citizen.",
                },
            },
            // --- RIGHT -----------------------------------------------------
            new string[][]
            {
                // right + progressive
                new string[]
                {
                    "A victory for liberty and prosperity.",
                    "The future belongs to the free.",
                },
                // right + traditional
                new string[]
                {
                    "A clear mandate to restore our values.",
                    "The silent majority has spoken.",
                },
            },
        };

        private static readonly string[][][] DefeatPools = new string[][][]
        {
            // --- LEFT ------------------------------------------------------
            new string[][]
            {
                // left + progressive
                new string[]
                {
                    "The movement doesn't stop at an election.",
                    "We keep organizing. We keep fighting.",
                },
                // left + traditional
                new string[]
                {
                    "The workers' fight never ends.",
                    "We regroup. We organize. We come back.",
                },
            },
            // --- CENTER ----------------------------------------------------
            new string[][]
            {
                // center + progressive
                new string[]
                {
                    "We accept the result. The work continues.",
                    "Good ideas don't lose. They wait.",
                },
                // center + traditional
                new string[]
                {
                    "We thank our voters and stand as loyal opposition.",
                    "Democracy spoke. We listen.",
                },
            },
            // --- RIGHT -----------------------------------------------------
            new string[][]
            {
                // right + progressive
                new string[]
                {
                    "Freedom is a long game. We're patient.",
                    "Markets correct. So will politics.",
                },
                // right + traditional
                new string[]
                {
                    "We fight on for the heart of our city.",
                    "We accept the result. The struggle for our values continues.",
                },
            },
        };

        public static void DriftCampaign(float dayDelta)
        {
            var st = PoliticsState.Instance;
            if (st == null || st.CurrentSupport == null) return;
            int n = PartyCountRef.Value;
            if (n <= 0) return;

            // Sample a few citizens each tick; update support gradually.
            int samples = Math.Max(50, (int)(Config.VoterSampleSize * dayDelta / RuntimeConfig.CampaignLengthDays));
            float[] tally = new float[n];
            int actualSamples = SampleCitizenPreferences(samples, tally);
            if (actualSamples == 0) return;

            // Normalize tally to shares
            float total = 0f;
            for (int i = 0; i < tally.Length; i++) total += tally[i];
            if (total <= 0f) return;
            for (int i = 0; i < tally.Length; i++) tally[i] /= total;

            // Blend toward sampled preferences (lerp factor proportional to dayDelta)
            float alpha = Mathf.Clamp01(dayDelta * 0.2f);
            int lim = Math.Min(st.CurrentSupport.Length, tally.Length);
            for (int i = 0; i < lim; i++)
            {
                st.CurrentSupport[i] = Mathf.Lerp(st.CurrentSupport[i], tally[i], alpha);
            }
        }

        public static void RunElection()
        {
            try
            {
                var st = PoliticsState.Instance;
                if (st == null) return;
                // Full sample to compute final results + per-building dominant party.
                int sampled = RunFullElectionSample();
                if (sampled == 0)
                {
                    PoliticsUserMod.Log("No voters sampled - aborting election.");
                    st.Phase = ElectionPhase.Idle;
                    st.DaysSinceLastElection = 0f;
                    return;
                }

                // Fix parliament size for the duration of this term based on current population
                int pop = CitizenManagerUtil.GetPopulation();
                int targetSeats = Config.CalculateParliamentSeats(pop);
                st.ActiveParliamentSeats = targetSeats;

                // Allocate seats using largest remainders method
                int np = Config.Parties != null ? Config.Parties.Length : PartyCountRef.Value;
                if (st.CurrentSupport == null || st.CurrentSupport.Length != np)
                {
                    var aligned = new float[np];
                    if (st.CurrentSupport != null)
                    {
                        int c = Math.Min(st.CurrentSupport.Length, np);
                        for (int i = 0; i < c; i++) aligned[i] = st.CurrentSupport[i];
                    }
                    st.CurrentSupport = aligned;
                }
                var finalShares = (float[])st.CurrentSupport.Clone();
                AllocateSeats(finalShares, targetSeats, out st.CurrentSeats);

                // Compute Senate seats: 1 seat per administrative district (districtId >= 1)
                int[] senateSeats = new int[np];
                int totalSenateSeats = 0;
                if (_lastDistrictResults != null)
                {
                    foreach (var dr in _lastDistrictResults)
                    {
                        if (dr.DistrictId == 0) continue; // Unzoned area does not get a Senate seat
                        int bestParty = -1;
                        int bestVotes = -1;
                        for (int p = 0; p < np; p++)
                        {
                            if (dr.VotesByParty != null && p < dr.VotesByParty.Length && dr.VotesByParty[p] > bestVotes)
                            {
                                bestVotes = dr.VotesByParty[p];
                                bestParty = p;
                            }
                        }
                        dr.SenateWinnerParty = bestParty;
                        if (bestParty >= 0 && bestParty < np && bestParty < senateSeats.Length)
                        {
                            senateSeats[bestParty]++;
                            totalSenateSeats++;
                        }
                    }
                }
                st.ActiveSenateSeats = (int[])senateSeats.Clone();

                // Build result
                var now = SimulationManager.instance.m_currentGameTime;
                var result = new ElectionResult
                {
                    Year = now.Year,
                    Month = now.Month,
                    ParliamentSeatsTotal = targetSeats,
                    SeatsByParty = (int[])st.CurrentSeats.Clone(),
                    VoteShareByParty = (float[])finalShares.Clone(),
                    Turnout = ComputeTurnout(),
                    SenateSeatsByParty = (int[])senateSeats.Clone(),
                    TotalSenateSeats = totalSenateSeats,
                    DistrictResults = _lastDistrictResults != null ? new List<DistrictResult>(_lastDistrictResults) : new List<DistrictResult>(),
                    VotesByGrievance = _lastGrievanceTally != null
                                       ? (int[])_lastGrievanceTally.Clone()
                                       : new int[9],
                    VotesByAgeParty = _lastAgeTally != null ? (int[,])_lastAgeTally.Clone() : null,
                    VotesByEduParty = _lastEduTally != null ? (int[,])_lastEduTally.Clone() : null,
                    VotesByWealthParty = _lastWealthTally != null ? (int[,])_lastWealthTally.Clone() : null,
                };

                st.LastResult = result;
                st.History.Add(result);

                // Phase -> forming
                st.Phase = ElectionPhase.Forming;
                FormCoalition(result);
            }
            catch (Exception ex)
            {
                PoliticsUserMod.Log("RunElection caught exception: " + ex);
            }
        }

        private static float ComputeTurnout()
        {
            try
            {
                var dm = Singleton<DistrictManager>.instance;
                float happiness = dm.m_districts.m_buffer[0].m_finalHappiness / 100f;
                return Mathf.Clamp01(Config.TurnoutBase + happiness * Config.TurnoutHappinessBoost);
            }
            catch { return Config.TurnoutBase; }
        }

        private static void FormCoalition(ElectionResult result)
        {
            try
            {
                var st = PoliticsState.Instance;
                if (st == null) return;
                if (st.CurrentSeats == null) st.CurrentSeats = new int[Config.Parties != null ? Config.Parties.Length : 0];
                if (st.CoalitionPartyIds == null) st.CoalitionPartyIds = new List<int>();
                st.CoalitionPartyIds.Clear();

                int nParties = Math.Min(st.CurrentSeats.Length, Config.Parties != null ? Config.Parties.Length : 0);
                if (nParties <= 0) return;

                // Start with the largest party; greedily add closest-ideology partners until majority.
                var parties = new List<int>();
                for (int i = 0; i < nParties; i++) parties.Add(i);
                parties.Sort((a, b) => st.CurrentSeats[b].CompareTo(st.CurrentSeats[a]));

                int seatsTotal = 0;
                var chosen = new List<int>();
                int lead = parties[0];
                chosen.Add(lead);
                seatsTotal += st.CurrentSeats[lead];

                while (seatsTotal < Config.MajorityThreshold &&
                       chosen.Count < Config.MaxCoalitionPartners)
                {
                    // pick the party closest in ideology to the average of chosen parties
                    Vector3 avg = AverageIdeology(chosen);
                    int best = -1;
                    float bestDist = float.MaxValue;
                    foreach (var p in parties)
                    {
                        if (chosen.Contains(p)) continue;
                        if (p < 0 || p >= Config.Parties.Length || Config.Parties[p] == null) continue;
                        if (st.CurrentSeats[p] <= 0) continue;
                        float d = (Config.Parties[p].Ideology - avg).magnitude;
                        if (d < bestDist) { bestDist = d; best = p; }
                    }
                    if (best < 0) break;
                    chosen.Add(best);
                    seatsTotal += st.CurrentSeats[best];
                }

                if (seatsTotal >= Config.MajorityThreshold && chosen.Count > 0 && chosen[0] >= 0 && chosen[0] < Config.Parties.Length)
                {
                    st.CoalitionPartyIds = chosen;
                    result.CoalitionPartyIds = new List<int>(chosen);
                    st.Phase = ElectionPhase.Governing;
                    st.DaysSinceLastElection = 0f;
                    ApplyCoalitionPolicies();
                    ShowResultsPopup(result);

                    var leadParty = (chosen.Count > 0 && Config.Parties != null && chosen[0] >= 0 && chosen[0] < Config.Parties.Length) ? Config.Parties[chosen[0]] : null;
                    if (leadParty == null) return;
                    bool isKo = L10n.CurrentCode.StartsWith("ko");
                    string toastMsg = isKo
                        ? string.Format(L10n.T(L10nKeys.Chirp_Toast_Coalition), leadParty.FullName, chosen.Count - 1)
                        : leadParty.FullName + " forms government with " + (chosen.Count - 1) + " partner(s).";
                    ShowToast(toastMsg);
                    // Refresh building tints so the new per-building dominant party shows up immediately
                    HarmonyPatcher.RefreshBuildingColors();

                    // Chirper announcements: news (always) + per-party reactions (suppressed in MinimalChirps)
                    string newsSender = L10n.T(L10nKeys.Chirp_CityNews_Sender);
                    string newsText = L10n.T(L10nKeys.Chirp_Election_Result,
                        leadParty.ShortName,
                        chosen.Count - 1,
                        (int)(result.Turnout * 100));
                    PostChirp(newsSender, newsText, 0u);

                    if (!DebugFlags.MinimalChirps && Config.Parties != null)
                    {
                        var winners = new HashSet<int>(chosen);
                        for (int i = 0; i < Config.Parties.Length; i++)
                        {
                            var party = Config.Parties[i];
                            if (party == null) continue;
                            string slogan = PickSloganForParty(i, winners.Contains(i) ? "victory" : "defeat");
                            PostChirp(party.FullName, slogan, 0u);
                        }
                    }
                }
                else
                {
                    // Failed: snap re-election after cooldown
                    st.Phase = ElectionPhase.Failed;
                    int coolDays = (int)RuntimeConfig.ReElectionCooldownDays;
                    st.FailedCooldownRemaining = coolDays;
                    RevertCoalitionPolicies();

                    bool isKo = L10n.CurrentCode.StartsWith("ko");
                    string failToast = isKo
                        ? string.Format(L10n.T(L10nKeys.Chirp_Toast_Failed), coolDays)
                        : "No coalition could be formed. Snap re-election in " + coolDays + " days.";
                    ShowToast(failToast);

                    string newsSender = L10n.T(L10nKeys.Chirp_CityNews_Sender);
                    string newsText = L10n.T(L10nKeys.Chirp_Election_Failed, coolDays);
                    PostChirp(newsSender, newsText, 0u);
                }
            }
            catch (Exception ex)
            {
                PoliticsUserMod.Log("FormCoalition caught exception: " + ex);
            }
        }

        private static Vector3 AverageIdeology(List<int> partyIds)
        {
            Vector3 v = Vector3.zero;
            int count = 0;
            if (partyIds != null && Config.Parties != null)
            {
                foreach (var id in partyIds)
                {
                    if (id >= 0 && id < Config.Parties.Length && Config.Parties[id] != null)
                    {
                        v += Config.Parties[id].Ideology;
                        count++;
                    }
                }
            }
            if (count > 0) v /= count;
            return v;
        }

        private static void AllocateSeats(float[] shares, int totalSeats, out int[] seats)
        {
            seats = new int[shares.Length];
            float[] quotas = new float[shares.Length];
            int allocated = 0;
            for (int i = 0; i < shares.Length; i++)
            {
                quotas[i] = shares[i] * totalSeats;
                seats[i] = (int)Math.Floor(quotas[i]);
                allocated += seats[i];
            }
            // Distribute remainders
            var remainders = new List<KeyValuePair<int, float>>();
            for (int i = 0; i < shares.Length; i++)
                remainders.Add(new KeyValuePair<int, float>(i, quotas[i] - seats[i]));
            remainders.Sort((a, b) => b.Value.CompareTo(a.Value));
            int idx = 0;
            while (allocated < totalSeats && idx < remainders.Count)
            {
                seats[remainders[idx].Key]++;
                allocated++;
                idx++;
            }
        }

        /// <summary>
        ///  Sample citizens and accumulate party preferences.
        ///  Returns number of citizens actually sampled.
        /// </summary>
        private static int SampleCitizenPreferences(int maxSamples, float[] tally)
        {
            var cm = Singleton<CitizenManager>.instance;
            var bm = Singleton<BuildingManager>.instance;
            if (cm == null || bm == null || cm.m_citizens.m_buffer == null) return 0;
            uint bufSize = (uint)Math.Min((int)cm.m_citizens.m_size, cm.m_citizens.m_buffer.Length);
            if (bufSize <= 1) return 0;
            int sampled = 0;
            int tries = 0;
            int limit = maxSamples * 4;
            while (sampled < maxSamples && tries < limit)
            {
                tries++;
                uint idx = (uint)_rng.Next(1, (int)bufSize);
                if (idx >= cm.m_citizens.m_buffer.Length) continue;
                var c = cm.m_citizens.m_buffer[idx];
                if ((c.m_flags & Citizen.Flags.Created) == 0) continue;
                if ((c.m_flags & Citizen.Flags.DummyTraffic) != 0) continue;
                Grievance _gUnused1;
                int party = DecideVote(ref c, bm, out _gUnused1);
                if (party < 0 || party >= tally.Length) continue;
                tally[party] += 1f;
                sampled++;
            }
            return sampled;
        }

        private static int RunFullElectionSample()
        {
            var st = PoliticsState.Instance;
            var cm = Singleton<CitizenManager>.instance;
            var bm = Singleton<BuildingManager>.instance;
            if (cm == null || bm == null || bm.m_buildings.m_buffer == null || cm.m_units.m_buffer == null) return 0;

            uint bBuf = (uint)Math.Min((int)bm.m_buildings.m_size, bm.m_buildings.m_buffer.Length);
            if (bBuf == 0) return 0;

            // Per-building tallies (compact - residential buildings only)
            var perBuildingTally = new int[bBuf, PartyCountRef.Value];
            var perBuildingVoters = new int[bBuf];
            var perBuildingHappy = new int[bBuf];
            var overallTally = new float[PartyCountRef.Value];
            int totalSampled = 0;
            // Grievance tally for the current election - indexed by (int)Grievance.
            // Size = 9 matches the Grievance enum (None + 8 concrete values).
            var grievanceTally = new int[9];
            int _np = Config.Parties != null ? Config.Parties.Length : PartyCountRef.Value;
            if (_np <= 0) return 0;
            var ageTally = new int[3, _np];
            var eduTally = new int[4, _np];
            var wealthTally = new int[3, _np];

            // District tallies: 128 max districts.
            // 0 = unzoned outer city, 1..127 = administrative districts.
            int maxDistricts = 128;
            var districtVotesByParty = new int[maxDistricts, _np];
            var districtTotalVotes = new int[maxDistricts];
            var districtGrievance = new int[maxDistricts, 9];
            var districtAge = new int[maxDistricts, 3, _np];
            var districtEdu = new int[maxDistricts, 4, _np];
            var districtWealth = new int[maxDistricts, 3, _np];
            var dm = Singleton<DistrictManager>.instance;

            // Walk EVERY residential building and sample its citizen units.
            // This gives ~100% per-building coverage unlike random citizen sampling.
            // On 150k-citizen cities this runs once per election (~365 game days)
            // and takes <100ms - acceptable.
            for (int b = 1; b < bBuf; b++)
            {
                var building = bm.m_buildings.m_buffer[b];
                if ((building.m_flags & Building.Flags.Created) == 0) continue;
                if (building.Info == null) continue;
                if (building.Info.GetService() != ItemClass.Service.Residential) continue;

                byte districtId = 0;
                try
                {
                    districtId = dm.GetDistrict(building.m_position);
                }
                catch { districtId = 0; }
                if (districtId >= maxDistricts) districtId = 0;

                // Walk the citizen-unit linked list for this building.
                // Up to ~8 per building typically; hard-cap to prevent any runaway.
                uint unit = building.m_citizenUnits;
                int safety = 0;
                while (unit != 0u && safety < 256)
                {
                    safety++;
                    if (unit >= cm.m_units.m_buffer.Length) break;
                    var cu = cm.m_units.m_buffer[unit];
                    if ((cu.m_flags & CitizenUnit.Flags.Home) != 0)
                    {
                        // Up to 5 citizens per unit
                        SampleCitizenInUnit(cu.m_citizen0, cm, bm, b, districtId, perBuildingTally, perBuildingVoters, perBuildingHappy, districtVotesByParty, districtTotalVotes, districtGrievance, districtAge, districtEdu, districtWealth, overallTally, grievanceTally, ageTally, eduTally, wealthTally, ref totalSampled);
                        SampleCitizenInUnit(cu.m_citizen1, cm, bm, b, districtId, perBuildingTally, perBuildingVoters, perBuildingHappy, districtVotesByParty, districtTotalVotes, districtGrievance, districtAge, districtEdu, districtWealth, overallTally, grievanceTally, ageTally, eduTally, wealthTally, ref totalSampled);
                        SampleCitizenInUnit(cu.m_citizen2, cm, bm, b, districtId, perBuildingTally, perBuildingVoters, perBuildingHappy, districtVotesByParty, districtTotalVotes, districtGrievance, districtAge, districtEdu, districtWealth, overallTally, grievanceTally, ageTally, eduTally, wealthTally, ref totalSampled);
                        SampleCitizenInUnit(cu.m_citizen3, cm, bm, b, districtId, perBuildingTally, perBuildingVoters, perBuildingHappy, districtVotesByParty, districtTotalVotes, districtGrievance, districtAge, districtEdu, districtWealth, overallTally, grievanceTally, ageTally, eduTally, wealthTally, ref totalSampled);
                        SampleCitizenInUnit(cu.m_citizen4, cm, bm, b, districtId, perBuildingTally, perBuildingVoters, perBuildingHappy, districtVotesByParty, districtTotalVotes, districtGrievance, districtAge, districtEdu, districtWealth, overallTally, grievanceTally, ageTally, eduTally, wealthTally, ref totalSampled);
                    }
                    unit = cu.m_nextUnit;
                }
            }

            if (totalSampled == 0) return 0;

            // Write overall support
            float total = 0f;
            for (int i = 0; i < overallTally.Length; i++) total += overallTally[i];
            int sLim = Math.Min(st.CurrentSupport.Length, overallTally.Length);
            for (int i = 0; i < sLim; i++)
                st.CurrentSupport[i] = total > 0 ? overallTally[i] / total : 0f;

            // Resize per-building arrays if buffer grew
            var st2 = PoliticsState.Instance;
            if (st2.DominantPartyByBuilding == null || st2.DominantPartyByBuilding.Length != bBuf)
            {
                st2.DominantPartyByBuilding = new byte[bBuf];
                st2.TurnoutByBuilding = new byte[bBuf];
                st2.SatisfactionByBuilding = new byte[bBuf];
            }
            for (int b = 0; b < bBuf; b++)
            {
                int voters = perBuildingVoters[b];
                if (voters <= 0) { st2.DominantPartyByBuilding[b] = 255; continue; }
                int best = 0; int bestCt = -1;
                int pLim = Math.Min(PartyCountRef.Value, perBuildingTally.GetLength(1));
                for (int p = 0; p < pLim; p++)
                {
                    if (perBuildingTally[b, p] > bestCt) { bestCt = perBuildingTally[b, p]; best = p; }
                }
                st2.DominantPartyByBuilding[b] = (byte)best;
                var building = bm.m_buildings.m_buffer[b];
                int residents = BuildingResidentCount(building);
                // Everyone sampled voted (we're doing 100% coverage); turnout is
                // therefore a function of how many of the building's residents
                // were "created" citizens (exclude dummies, empty slots).
                st2.TurnoutByBuilding[b] = (byte)Mathf.Clamp(residents > 0 ? Math.Min(100, voters * 100 / residents) : 100, 0, 100);
                st2.SatisfactionByBuilding[b] = (byte)Mathf.Clamp(voters > 0 ? (perBuildingHappy[b] * 100 / voters) : 0, 0, 100);
            }
            _lastGrievanceTally = grievanceTally;
            _lastAgeTally = ageTally;
            _lastEduTally = eduTally;
            _lastWealthTally = wealthTally;

            // Build per-district results
            var dResults = new List<DistrictResult>();
            for (byte d = 0; d < maxDistricts; d++)
            {
                if (districtTotalVotes[d] <= 0) continue;

                string name;
                if (d == 0)
                {
                    name = Localization.L10n.T(Localization.L10nKeys.Stats_District_Unzoned);
                }
                else
                {
                    bool isCreated = false;
                    try
                    {
                        isCreated = (dm.m_districts.m_buffer[d].m_flags & District.Flags.Created) != 0;
                    }
                    catch { }
                    if (!isCreated) continue;

                    try
                    {
                        name = dm.GetDistrictName(d);
                        if (string.IsNullOrEmpty(name)) name = "District " + d;
                    }
                    catch { name = "District " + d; }
                }

                var dr = new DistrictResult
                {
                    DistrictId = d,
                    DistrictName = name,
                    TotalVotes = districtTotalVotes[d],
                    VotesByParty = new int[_np],
                    VoteShareByParty = new float[_np],
                    VotesByGrievance = new int[9],
                    VotesByAgeParty = new int[3, _np],
                    VotesByEduParty = new int[4, _np],
                    VotesByWealthParty = new int[3, _np]
                };

                for (int p = 0; p < _np; p++)
                {
                    dr.VotesByParty[p] = districtVotesByParty[d, p];
                    dr.VoteShareByParty[p] = dr.TotalVotes > 0 ? (float)dr.VotesByParty[p] / dr.TotalVotes : 0f;
                }
                for (int g = 0; g < 9; g++) dr.VotesByGrievance[g] = districtGrievance[d, g];
                for (int b = 0; b < 3; b++)
                    for (int p = 0; p < _np; p++)
                        dr.VotesByAgeParty[b, p] = districtAge[d, b, p];
                for (int b = 0; b < 4; b++)
                    for (int p = 0; p < _np; p++)
                        dr.VotesByEduParty[b, p] = districtEdu[d, b, p];
                for (int b = 0; b < 3; b++)
                    for (int p = 0; p < _np; p++)
                        dr.VotesByWealthParty[b, p] = districtWealth[d, b, p];

                dResults.Add(dr);
            }
            _lastDistrictResults = dResults;

            return totalSampled;
        }

        /// <summary>
        /// Rebuild building overlay data (dominant party, turnout, satisfaction)
        /// from resident citizens. Called on level load if overlay data is empty/missing
        /// but previous elections have taken place.
        /// </summary>
        public static void RebuildBuildingOverlayData()
        {
            try
            {
                var st = PoliticsState.Instance;
                if (st == null) return;
                var bm = Singleton<BuildingManager>.instance;
                var cm = Singleton<CitizenManager>.instance;
                if (bm == null || cm == null || bm.m_buildings.m_buffer == null || cm.m_units.m_buffer == null) return;

                uint bBuf = (uint)Math.Min((int)bm.m_buildings.m_size, bm.m_buildings.m_buffer.Length);
                if (bBuf == 0) return;

                if (st.DominantPartyByBuilding == null || st.DominantPartyByBuilding.Length != bBuf)
                {
                    st.DominantPartyByBuilding = new byte[bBuf];
                    st.TurnoutByBuilding = new byte[bBuf];
                    st.SatisfactionByBuilding = new byte[bBuf];
                    for (int i = 0; i < bBuf; i++) st.DominantPartyByBuilding[i] = 255;
                }

                int partyCount = PartyCountRef.Value;
                if (partyCount <= 0) return;

                var perBuildingTally = new int[bBuf, partyCount];
                var perBuildingVoters = new int[bBuf];
                var perBuildingHappy = new int[bBuf];

                for (int b = 1; b < bBuf; b++)
                {
                    var building = bm.m_buildings.m_buffer[b];
                    if ((building.m_flags & Building.Flags.Created) == 0) continue;
                    if (building.Info == null || building.Info.GetService() != ItemClass.Service.Residential) continue;

                    uint unit = building.m_citizenUnits;
                    int safety = 0;
                    while (unit != 0u && safety < 256)
                    {
                        safety++;
                        if (unit >= cm.m_units.m_buffer.Length) break;
                        var cu = cm.m_units.m_buffer[unit];
                        if ((cu.m_flags & CitizenUnit.Flags.Home) != 0)
                        {
                            SampleCitizenSimple(cu.m_citizen0, cm, bm, b, perBuildingTally, perBuildingVoters, perBuildingHappy);
                            SampleCitizenSimple(cu.m_citizen1, cm, bm, b, perBuildingTally, perBuildingVoters, perBuildingHappy);
                            SampleCitizenSimple(cu.m_citizen2, cm, bm, b, perBuildingTally, perBuildingVoters, perBuildingHappy);
                            SampleCitizenSimple(cu.m_citizen3, cm, bm, b, perBuildingTally, perBuildingVoters, perBuildingHappy);
                            SampleCitizenSimple(cu.m_citizen4, cm, bm, b, perBuildingTally, perBuildingVoters, perBuildingHappy);
                        }
                        unit = cu.m_nextUnit;
                    }
                }

                int pLim = Math.Min(partyCount, perBuildingTally.GetLength(1));
                for (int b = 0; b < bBuf; b++)
                {
                    int voters = perBuildingVoters[b];
                    if (voters <= 0) { st.DominantPartyByBuilding[b] = 255; continue; }
                    int best = 0; int bestCt = -1;
                    for (int p = 0; p < pLim; p++)
                    {
                        if (perBuildingTally[b, p] > bestCt) { bestCt = perBuildingTally[b, p]; best = p; }
                    }
                    st.DominantPartyByBuilding[b] = (byte)best;
                    var building = bm.m_buildings.m_buffer[b];
                    int residents = BuildingResidentCount(building);
                    st.TurnoutByBuilding[b] = (byte)Mathf.Clamp(residents > 0 ? Math.Min(100, voters * 100 / residents) : 100, 0, 100);
                    st.SatisfactionByBuilding[b] = (byte)Mathf.Clamp(voters > 0 ? (perBuildingHappy[b] * 100 / voters) : 0, 0, 100);
                }

                HarmonyPatcher.RefreshBuildingColors();
            }
            catch (Exception ex)
            {
                PoliticsUserMod.Log("RebuildBuildingOverlayData caught exception: " + ex);
            }
        }

        private static void SampleCitizenSimple(uint citizenId, CitizenManager cm, BuildingManager bm,
                                                int buildingId, int[,] perBuildingTally,
                                                int[] perBuildingVoters, int[] perBuildingHappy)
        {
            if (citizenId == 0u || buildingId < 0) return;
            if (perBuildingTally == null || buildingId >= perBuildingTally.GetLength(0)) return;
            if (perBuildingVoters == null || buildingId >= perBuildingVoters.Length) return;
            if (perBuildingHappy == null || buildingId >= perBuildingHappy.Length) return;
            if (cm == null || cm.m_citizens.m_buffer == null || citizenId >= cm.m_citizens.m_buffer.Length) return;

            var c = cm.m_citizens.m_buffer[citizenId];
            if ((c.m_flags & Citizen.Flags.Created) == 0) return;
            if ((c.m_flags & Citizen.Flags.DummyTraffic) != 0) return;

            Grievance reason;
            int party = DecideVote(ref c, bm, out reason);
            if (party < 0 || party >= perBuildingTally.GetLength(1)) return;

            perBuildingTally[buildingId, party]++;
            perBuildingVoters[buildingId]++;
            if ((c.m_flags & Citizen.Flags.NeedGoods) == 0) perBuildingHappy[buildingId]++;
        }

        private static void SampleCitizenInUnit(uint citizenId, CitizenManager cm, BuildingManager bm,
                                                int buildingId, byte districtId, int[,] perBuildingTally,
                                                int[] perBuildingVoters, int[] perBuildingHappy,
                                                int[,] districtVotesByParty, int[] districtTotalVotes,
                                                int[,] districtGrievance, int[,,] districtAge,
                                                int[,,] districtEdu, int[,,] districtWealth,
                                                float[] overallTally, int[] grievanceTally,
                                                int[,] ageTally, int[,] eduTally, int[,] wealthTally,
                                                ref int totalSampled)
        {
            if (citizenId == 0u || buildingId < 0) return;
            if (cm == null || cm.m_citizens.m_buffer == null || citizenId >= cm.m_citizens.m_buffer.Length) return;

            var c = cm.m_citizens.m_buffer[citizenId];
            if ((c.m_flags & Citizen.Flags.Created) == 0) return;
            if ((c.m_flags & Citizen.Flags.DummyTraffic) != 0) return;

            Grievance reason;
            int party = DecideVote(ref c, bm, out reason);
            if (party < 0) return;

            if (overallTally != null && party < overallTally.Length) overallTally[party] += 1f;
            if (perBuildingTally != null && buildingId < perBuildingTally.GetLength(0) && party < perBuildingTally.GetLength(1)) perBuildingTally[buildingId, party]++;
            if (perBuildingVoters != null && buildingId < perBuildingVoters.Length) perBuildingVoters[buildingId]++;
            if (perBuildingHappy != null && buildingId < perBuildingHappy.Length && (c.m_flags & Citizen.Flags.NeedGoods) == 0) perBuildingHappy[buildingId]++;

            // Per-district tallies
            if (districtVotesByParty != null && districtId < districtVotesByParty.GetLength(0) && party < districtVotesByParty.GetLength(1))
            {
                districtVotesByParty[districtId, party]++;
            }
            if (districtTotalVotes != null && districtId < districtTotalVotes.Length)
            {
                districtTotalVotes[districtId]++;
            }

            if (grievanceTally != null)
            {
                int gi = (int)reason;
                if (gi >= 0 && gi < grievanceTally.Length)
                {
                    grievanceTally[gi]++;
                    if (districtGrievance != null && districtId < districtGrievance.GetLength(0) && gi < districtGrievance.GetLength(1))
                    {
                        districtGrievance[districtId, gi]++;
                    }
                }
            }

            // Demographic cross-tabs
            int np = Config.Parties != null ? Config.Parties.Length : PartyCountRef.Value;
            if (ageTally != null && party < np && party < ageTally.GetLength(1))
            {
                var age = Citizen.GetAgeGroup(c.m_age);
                int bucket = (age == Citizen.AgeGroup.Young) ? 0 :
                             (age == Citizen.AgeGroup.Adult) ? 1 :
                             (age == Citizen.AgeGroup.Senior) ? 2 : -1;
                if (bucket >= 0 && bucket < ageTally.GetLength(0))
                {
                    ageTally[bucket, party]++;
                    if (districtAge != null && districtId < districtAge.GetLength(0) && bucket < districtAge.GetLength(1) && party < districtAge.GetLength(2))
                    {
                        districtAge[districtId, bucket, party]++;
                    }
                }
            }
            if (eduTally != null && party < np && party < eduTally.GetLength(1))
            {
                int edu = (int)c.EducationLevel; // 0..3
                if (edu < 0) edu = 0; if (edu > 3) edu = 3;
                if (edu < eduTally.GetLength(0))
                {
                    eduTally[edu, party]++;
                    if (districtEdu != null && districtId < districtEdu.GetLength(0) && edu < districtEdu.GetLength(1) && party < districtEdu.GetLength(2))
                    {
                        districtEdu[districtId, edu, party]++;
                    }
                }
            }
            if (wealthTally != null && party < np && party < wealthTally.GetLength(1))
            {
                int w = (int)c.WealthLevel; // 0..2
                if (w < 0) w = 0; if (w > 2) w = 2;
                if (w < wealthTally.GetLength(0))
                {
                    wealthTally[w, party]++;
                    if (districtWealth != null && districtId < districtWealth.GetLength(0) && w < districtWealth.GetLength(1) && party < districtWealth.GetLength(2))
                    {
                        districtWealth[districtId, w, party]++;
                    }
                }
            }

            totalSampled++;
        }

        private static int BuildingResidentCount(Building b)
        {
            // Approximate - BuildingAI.CalculateHomeCount would be exact but varies by type.
            // Household count inferred via Citizen unit walk would be more accurate but slower.
            // Use the building's current citizen count summary.
            var info = b.Info;
            if (info == null || info.m_buildingAI == null) return 0;
            return b.m_citizenCount;
        }

        /// <summary>
        /// Decide which party a citizen votes for.
        /// Returns -1 if the citizen is ineligible to vote (children / teens).
        /// <summary>
        /// Decide which party a citizen votes for.
        /// Returns -1 for children/teens (non-voters).
        /// Combines:
        ///   * Ideology (from VoterTraits "nudges" + party.Ideology) - the
        ///     baseline leaning.
        ///   * Grievances (real-game-state complaints) - strong pull toward
        ///     parties whose platform addresses the grievance.
        ///   * Random noise for variety.
        /// Sets <paramref name="reason"/> to the dominant grievance that
        /// drove the choice, or Grievance.None if pure ideology.
        /// </summary>
        public static int DecideVote(ref Citizen c, BuildingManager bm, out Grievance reason)
        {
            reason = Grievance.None;

            // Voting eligibility
            var ageGroup = Citizen.GetAgeGroup(c.m_age);
            if (ageGroup == Citizen.AgeGroup.Child || ageGroup == Citizen.AgeGroup.Teen)
                return -1;

            int wealth = (int)c.WealthLevel;
            int education = (int)c.EducationLevel;
            bool employed = (c.m_workBuilding != 0);
            bool sick = (c.m_flags & Citizen.Flags.Sick) != 0;

            // ---- Ideology nudges: build a voter ideology point from traits ----
            float econ = 0f;
            switch (education)
            {
                case 0: econ += VoterTraits.BiasEduUneducated; break;
                case 1: econ += VoterTraits.BiasEduEducated; break;
                case 2: econ += VoterTraits.BiasEduWellEducated; break;
                default: econ += VoterTraits.BiasEduHighlyEducated; break;
            }
            switch (wealth)
            {
                case 0: econ += VoterTraits.BiasWealthLow; break;
                case 1: econ += VoterTraits.BiasWealthMedium; break;
                default: econ += VoterTraits.BiasWealthHigh; break;
            }
            econ += employed ? VoterTraits.BiasEmployed : VoterTraits.BiasUnemployed;
            if (ageGroup == Citizen.AgeGroup.Young) econ += VoterTraits.BiasYoung;
            else if (ageGroup == Citizen.AgeGroup.Adult) econ += VoterTraits.BiasAdult;
            else if (ageGroup == Citizen.AgeGroup.Senior) econ += VoterTraits.BiasSenior;
            if (sick) econ += VoterTraits.BiasSick;
            // Deficit pressure - the city is losing money → voters drift right
            // (lower taxes, business-friendly). Strength 0..0.35 based on how
            // many consecutive weeks the budget has been negative.
            econ += DeficitPressure;

            econ = Mathf.Clamp(econ, -1f, 1f);

            float ageF = Mathf.Clamp01(c.m_age / 240f);
            float soc = Mathf.Clamp(ageF * 1.2f - 0.4f - (education * 0.15f), -1f, 1f);
            float gov = Mathf.Clamp((_rng.Next(0, 100) - 50) / 100f, -1f, 1f);

            // Small random jitter (half the old VoterNoise magnitude - rest of
            // the randomness lives in the combined-score noise step below).
            econ += ((float)_rng.NextDouble() - 0.5f) * Config.VoterNoise;
            soc += ((float)_rng.NextDouble() - 0.5f) * Config.VoterNoise;
            gov += ((float)_rng.NextDouble() - 0.5f) * Config.VoterNoise;

            var voterPoint = new Vector3(econ, soc, gov);

            // ---- Compute grievances ----
            var em = Singleton<EconomyManager>.instance;
            var grievances = Grievances.Compute(ref c, bm, em);

            // ---- Score each party: ideology_fit (0..1) + grievance_score ----
            // 40% ideology / 50% grievance / 10% noise (your requested weights).
            int best = -1;
            float bestScore = float.MinValue;
            Grievance bestReason = Grievance.None;
            int np = Config.Parties.Length;
            for (int i = 0; i < np; i++)
            {
                var party = Config.Parties[i];

                // Ideology fit: 1 - normalized_distance. Max distance in a
                // [-1,1]^3 cube is sqrt(12) ≈ 3.46 - normalize by that.
                float dist = (party.Ideology - voterPoint).magnitude;
                float ideologyFit = Mathf.Clamp01(1f - dist / 3.46f);

                // Grievance score: weighted sum of per-grievance fits.
                float grievScore = 0f;
                float grievWeight = 0f;
                Grievance localReason = Grievance.None;
                float localReasonFit = float.MinValue;
                foreach (var gv in grievances)
                {
                    float s = Grievances.ScorePartyForGrievance(party, gv.Key);
                    grievScore += gv.Value * s;
                    grievWeight += gv.Value;
                    if (s > localReasonFit) { localReasonFit = s; localReason = gv.Key; }
                }
                if (grievWeight > 0f) grievScore /= grievWeight; // avg weighted

                // Random noise per (citizen, party) pair.
                float noise = ((float)_rng.NextDouble() - 0.5f) * 0.2f; // ±0.1

                float total = 0.4f * ideologyFit + 0.5f * (grievScore + 1f) * 0.5f + 0.1f * (noise + 0.1f);
                // grievScore ∈ [-1..+1] mapped into 0..1 via (x+1)/2.

                if (total > bestScore)
                {
                    bestScore = total;
                    best = i;
                    // Only record grievance-based reason if grievance had a
                    // strong positive contribution for the winner.
                    bestReason = (grievances.Count > 0 && localReasonFit > 0.2f)
                               ? localReason
                               : Grievance.None;
                }
            }

            // Incumbency bump: happy voters (health + wellbeing both decent)
            // sometimes reward the coalition. Probability is user-tunable
            // via RuntimeConfig.IncumbencyBonus (default 0.10, 0 = off).
            int happiness = Citizen.GetHappiness(c.m_health, c.m_wellbeing);
            bool happy = happiness >= 60;
            var st = PoliticsState.Instance;
            if (happy && st != null && st.CoalitionPartyIds != null && st.CoalitionPartyIds.Count > 0
                && RuntimeConfig.IncumbencyBonus > 0f
                && _rng.NextDouble() < RuntimeConfig.IncumbencyBonus)
            {
                int cand = st.CoalitionPartyIds[_rng.Next(0, st.CoalitionPartyIds.Count)];
                if (cand >= 0 && cand < Config.Parties.Length)
                {
                    best = cand;
                    bestReason = Grievance.None; // incumbency = ideology-like
                }
            }

            if (best < 0 || best >= Config.Parties.Length)
            {
                best = -1;
                reason = Grievance.None;
            }
            else
            {
                reason = bestReason;
            }
            return best;
        }

        // ---- Policy application ------------------------------------------

        public static void ApplyCoalitionPolicies()
        {
            var st = PoliticsState.Instance;
            if (st == null) return;
            if (st.AppliedVanillaPolicies == null)
                st.AppliedVanillaPolicies = new List<DistrictPolicies.Policies>();

            // Snapshot the previous coalition's applied policies BEFORE diffing
            var prevApplied = new HashSet<DistrictPolicies.Policies>(st.AppliedVanillaPolicies);

            if (st.CoalitionPartyIds == null || st.CoalitionPartyIds.Count == 0)
            {
                RevertCoalitionPolicies(skipLog: false);
                return;
            }

            var dm = Singleton<DistrictManager>.instance;

            // Opposed policies across the coalition. Opposition WINS over support.
            var opposed = new HashSet<DistrictPolicies.Policies>();
            foreach (var id in st.CoalitionPartyIds)
            {
                if (id < 0 || Config.Parties == null || id >= Config.Parties.Length || Config.Parties[id] == null) continue;
                var arr = Config.Parties[id].OpposedPolicies;
                if (arr == null) continue;
                foreach (var p in arr) opposed.Add(p);
            }

            // Wanted = union of supported platforms, minus anything opposed.
            var wanted = new HashSet<DistrictPolicies.Policies>();
            foreach (var id in st.CoalitionPartyIds)
            {
                if (id < 0 || Config.Parties == null || id >= Config.Parties.Length || Config.Parties[id] == null) continue;
                var arr = Config.Parties[id].VanillaPolicies;
                if (arr == null) continue;
                foreach (var p in arr)
                {
                    if (!opposed.Contains(p)) wanted.Add(p);
                }
            }

            var newAppliedList = new List<DistrictPolicies.Policies>();

            // -------- Repeals: policies that were previously applied by the mod OR currently active but opposed ----
            // 1) Previously applied by mod, but no longer wanted in new platform
            foreach (var p in prevApplied)
            {
                if (!wanted.Contains(p) || opposed.Contains(p))
                {
                    var capture = p;
                    try
                    {
                        Singleton<SimulationManager>.instance.AddAction(() =>
                        {
                            try { Singleton<DistrictManager>.instance.UnsetCityPolicy(capture); }
                            catch (Exception ex) { PoliticsUserMod.Log("UnsetCityPolicy(" + capture + ") failed: " + ex.Message); }
                        });
                    }
                    catch { /* ignore */ }

                    PoliticsUserMod.Log("Repealed policy: " + p);
                    AnnounceRepeal(p, st);
                }
            }

            // 2) Policies explicitly opposed by new coalition, if currently active in the city
            foreach (var p in opposed)
            {
                if (prevApplied.Contains(p)) continue; // Already announced above
                bool isActive = false;
                try { isActive = dm.m_districts.m_buffer[0].IsPolicySet(p); } catch { }
                if (isActive)
                {
                    var capture = p;
                    try
                    {
                        Singleton<SimulationManager>.instance.AddAction(() =>
                        {
                            try { Singleton<DistrictManager>.instance.UnsetCityPolicy(capture); }
                            catch (Exception ex) { PoliticsUserMod.Log("UnsetCityPolicy(" + capture + ") failed: " + ex.Message); }
                        });
                    }
                    catch { /* ignore */ }

                    PoliticsUserMod.Log("Repealed (opposed pre-existing) policy: " + p);
                    AnnounceRepeal(p, st);
                }
            }

            // -------- Enact / Retain policies --------------------------------
            foreach (var p in wanted)
            {
                try
                {
                    bool already = dm.m_districts.m_buffer[0].IsPolicySet(p);
                    newAppliedList.Add(p);

                    if (!already)
                    {
                        // Brand new policy: enact in game and announce bill!
                        var capture = p;
                        Singleton<SimulationManager>.instance.AddAction(() =>
                        {
                            try { Singleton<DistrictManager>.instance.SetCityPolicy(capture); }
                            catch (Exception ex) { PoliticsUserMod.Log("SetCityPolicy(" + capture + ") failed: " + ex.Message); }
                        });

                        PoliticsUserMod.Log("Enacted new policy: " + p);
                        AnnounceBill(p, st);
                    }
                    else
                    {
                        // Policy is ALREADY active in the city.
                        // Do NOT re-announce an already active policy!
                        PoliticsUserMod.Log("Policy " + p + " is already active - retaining silently without re-announcement.");
                    }
                }
                catch (Exception e)
                {
                    PoliticsUserMod.Log("Could not evaluate policy " + p + ": " + e.Message);
                }
            }

            st.AppliedVanillaPolicies = newAppliedList;

            // Reapply custom modifiers (tax/budget diff)
            if (st.PoliciesApplied)
            {
                ApplyCustomModifiers(-1);
            }
            ApplyCustomModifiers(+1);
            st.PoliciesApplied = true;
            PoliticsUserMod.Log("Applied coalition policies. Count=" + st.AppliedVanillaPolicies.Count);

            AnnounceBudgetAndTaxBill(st);
            RefreshEconomyPanel();
            RefreshCityPoliciesPanel();
        }

        /// <summary>
        /// Force the vanilla Economy panel to re-read tax/budget values.
        /// The Economy panel reads its values in Start/OnEnable, so toggling
        /// visibility does not help. Instead we walk the UI tree looking for
        /// components with tax/budget-related names and invoke refresh
        /// methods on each via reflection.
        /// </summary>
        private static void RefreshEconomyPanel()
        {
            try
            {
                // The Economy panel is an EconomyPanel instance. We want to
                // call its private "PopulateXxx" methods to force a re-read
                // from EconomyManager.

                var type = Type.GetType("EconomyPanel, Assembly-CSharp", false);
                if (type != null)
                {
                    var obj = UnityEngine.Object.FindObjectOfType(type);
                    if (obj != null)
                    {
                        var t = obj.GetType();
                        // Try every plausible zero-arg instance method in EconomyPanel.
                        string[] names = new[]
                        {
                            "PopulateData", "Populate", "PopulateTaxRate",
                            "PopulateBudget", "RefreshTaxRate", "RefreshBudget",
                            "Invalidate", "RefreshPanel", "RefreshContent",
                            "UpdateTexts", "UpdateValues"
                        };
                        int hit = 0;
                        foreach (var n in names)
                        {
                            var m = t.GetMethod(n,
                                System.Reflection.BindingFlags.Instance |
                                System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.NonPublic);
                            if (m == null) continue;
                            if (m.GetParameters().Length != 0) continue;
                            try { m.Invoke(obj, null); PoliticsUserMod.Log("EconomyPanel." + n + "() called"); hit++; }
                            catch (Exception ex) { PoliticsUserMod.Log("EconomyPanel." + n + " threw: " + ex.Message); }
                        }
                        if (hit == 0)
                        {
                            // Fallback: enumerate all instance methods and log names so we can see what's there.
                            var methods = t.GetMethods(System.Reflection.BindingFlags.Instance |
                                                       System.Reflection.BindingFlags.Public |
                                                       System.Reflection.BindingFlags.NonPublic);
                            int logged = 0;
                            foreach (var m in methods)
                            {
                                if (m.GetParameters().Length != 0) continue;
                                if (m.DeclaringType != t) continue;
                                PoliticsUserMod.Log("EconomyPanel candidate: " + m.Name);
                                if (++logged >= 40) break;
                            }
                        }
                    }
                    else
                    {
                        PoliticsUserMod.Log("EconomyPanel type found but no instance in scene");
                    }
                }
                else
                {
                    PoliticsUserMod.Log("EconomyPanel type not found via Type.GetType");
                }
            }
            catch (Exception e)
            {
                PoliticsUserMod.Log("RefreshEconomyPanel failed: " + e.Message);
            }
        }

        private static bool TryInvokeRefreshMethods(UIComponent comp)
        {
            bool any = false;
            string[] methodNames = new[] {
                "RefreshPanel", "Refresh", "Invalidate", "Populate",
                "RefreshContent", "RefreshValues", "UpdateValues",
                "PopulateData", "RefreshData"
            };
            var t = comp.GetType();
            foreach (var m in methodNames)
            {
                var mi = t.GetMethod(m, System.Reflection.BindingFlags.Instance |
                                        System.Reflection.BindingFlags.Public |
                                        System.Reflection.BindingFlags.NonPublic);
                if (mi == null) continue;
                if (mi.GetParameters().Length != 0) continue;
                try
                {
                    mi.Invoke(comp, null);
                    PoliticsUserMod.Log("Invoked " + comp.name + "." + m);
                    any = true;
                }
                catch { }
            }
            return any;
        }

        public static void RevertCoalitionPolicies(bool skipLog = false)
        {
            var st = PoliticsState.Instance;
            if (!st.PoliciesApplied) return;
            foreach (var p in st.AppliedVanillaPolicies)
            {
                var capture = p;
                try
                {
                    Singleton<SimulationManager>.instance.AddAction(() =>
                    {
                        try { Singleton<DistrictManager>.instance.UnsetCityPolicy(capture); }
                        catch { /* ignore */ }
                    });
                }
                catch { /* ignore */ }
            }
            st.AppliedVanillaPolicies.Clear();
            ApplyCustomModifiers(-1);
            st.PoliciesApplied = false;
            if (!skipLog) PoliticsUserMod.Log("Reverted coalition policies.");
        }

        /// <summary>
        /// Calculate a realistic parliamentary vote tally with tight margins (e.g. 52-45 or 54-43 with a few abstentions)
        /// reflecting realistic party discipline and opposition confrontation.
        /// </summary>
        public static void CalculateRealisticBillTally(
            PoliticsState st,
            HashSet<int> supporters,
            HashSet<int> opponents,
            bool isPass,
            out int yes,
            out int no,
            out int abstain)
        {
            yes = 0;
            no = 0;
            abstain = 0;

            var coalitionSet = new HashSet<int>(st.CoalitionPartyIds ?? new List<int>());
            int numParties = Config.Parties != null ? Config.Parties.Length : 0;
            int totalSeats = st.ActiveParliamentSeats > 0 ? st.ActiveParliamentSeats : 100;

            for (int i = 0; i < numParties; i++)
            {
                int seats = (st.CurrentSeats != null && i < st.CurrentSeats.Length) ? st.CurrentSeats[i] : 0;
                if (seats <= 0) continue;

                float yesShare;
                float abstainShare;

                bool isGov = coalitionSet.Contains(i);
                bool isSup = supporters != null && supporters.Contains(i);
                bool isOpp = opponents != null && opponents.Contains(i);

                if (isGov)
                {
                    if (isOpp)
                    {
                        // Internal coalition friction on opposed policy
                        yesShare = 0.30f + (float)_rng.NextDouble() * 0.15f;
                        abstainShare = 0.08f + (float)_rng.NextDouble() * 0.05f;
                    }
                    else
                    {
                        // Strong governing party whip: 90~95% yes, 2~4% abstain, 3~7% dissent
                        yesShare = 0.91f + (float)_rng.NextDouble() * 0.04f;
                        abstainShare = 0.02f + (float)_rng.NextDouble() * 0.03f;
                    }
                }
                else
                {
                    if (isSup)
                    {
                        // Opposition supports bipartisan bill
                        yesShare = 0.86f + (float)_rng.NextDouble() * 0.06f;
                        abstainShare = 0.03f + (float)_rng.NextDouble() * 0.03f;
                    }
                    else
                    {
                        // Standard opposition confrontation: 88~93% no, 3~7% yes, 2~5% abstain
                        abstainShare = 0.02f + (float)_rng.NextDouble() * 0.03f;
                        float noShare = 0.89f + (float)_rng.NextDouble() * 0.05f;
                        yesShare = Mathf.Max(0.02f, 1f - noShare - abstainShare);
                    }
                }

                int py = Mathf.RoundToInt(seats * yesShare);
                int pa = Mathf.RoundToInt(seats * abstainShare);
                int pn = Math.Max(0, seats - py - pa);

                yes += py;
                no += pn;
                abstain += pa;
            }

            int sum = yes + no + abstain;
            if (sum < totalSeats)
            {
                int diff = totalSeats - sum;
                no += diff;
            }

            // Realism adjustment: in parliamentary democracy, controversial bills pass with a realistic 51%~56% majority.
            int votingTotal = yes + no;
            if (votingTotal > 0)
            {
                if (isPass)
                {
                    // Target a realistic tight margin: 51% ~ 54% YES
                    float targetYesPct = 0.51f + (float)_rng.NextDouble() * 0.035f;
                    int targetYes = Mathf.Clamp(Mathf.RoundToInt(votingTotal * targetYesPct), (votingTotal / 2) + 1, votingTotal);
                    int currentDiff = targetYes - yes;
                    if (currentDiff > 0)
                    {
                        int shift = Math.Min(currentDiff, no);
                        yes += shift;
                        no -= shift;
                    }
                    else if (currentDiff < 0)
                    {
                        int shift = Math.Min(-currentDiff, yes - ((votingTotal / 2) + 1));
                        if (shift > 0)
                        {
                            yes -= shift;
                            no += shift;
                        }
                    }

                    // Strict sanity check: bill must pass if isPass is true
                    if (yes <= no)
                    {
                        int flip = (no - yes) / 2 + 1;
                        yes += flip;
                        no = Math.Max(0, no - flip);
                    }
                }
                else
                {
                    // Rejection: 51% ~ 54% NO
                    float targetNoPct = 0.51f + (float)_rng.NextDouble() * 0.035f;
                    int targetNo = Mathf.Clamp(Mathf.RoundToInt(votingTotal * targetNoPct), (votingTotal / 2) + 1, votingTotal);
                    int currentDiff = targetNo - no;
                    if (currentDiff > 0)
                    {
                        int shift = Math.Min(currentDiff, yes);
                        no += shift;
                        yes -= shift;
                    }
                    else if (currentDiff < 0)
                    {
                        int shift = Math.Min(-currentDiff, no - ((votingTotal / 2) + 1));
                        if (shift > 0)
                        {
                            no -= shift;
                            yes += shift;
                        }
                    }

                    if (no <= yes)
                    {
                        int flip = (yes - no) / 2 + 1;
                        no += flip;
                        yes = Math.Max(0, yes - flip);
                    }
                }
            }
        }

        /// <summary>
        /// Announce that a previously-active policy is being repealed by
        /// the incoming government.
        /// </summary>
        public static void AnnounceRepeal(DistrictPolicies.Policies policy, PoliticsState st)
        {
            var supporters = new HashSet<int>();
            var opponents = new HashSet<int>();
            if (Config.Parties != null)
            {
                for (int i = 0; i < Config.Parties.Length; i++)
                {
                    if (Config.Parties[i] == null) continue;
                    var sup = Config.Parties[i].VanillaPolicies;
                    if (sup != null)
                    {
                        foreach (var vp in sup) if (vp == policy) { opponents.Add(i); break; }
                    }
                    var opp = Config.Parties[i].OpposedPolicies;
                    if (opp != null)
                    {
                        foreach (var vp in opp) if (vp == policy) { supporters.Add(i); break; }
                    }
                }
            }
            if (st.CoalitionPartyIds != null)
            {
                foreach (var cid in st.CoalitionPartyIds)
                {
                    if (Config.Parties != null && cid >= 0 && cid < Config.Parties.Length) supporters.Add(cid);
                }
            }

            int yes, no, abstain;
            CalculateRealisticBillTally(st, supporters, opponents, true, out yes, out no, out abstain);

            int billNo = st.NextBillNumber++;
            string title = FormatPolicyTitle(policy);
            string abstentionStr = abstain > 0 ? L10n.T(L10nKeys.Chirp_Bill_Abstentions, abstain) : "";
            string text = L10n.T(L10nKeys.Chirp_Bill_Repealed, yes, no, billNo, title, abstentionStr);

            string sender = L10n.T(L10nKeys.Chirp_CityNews_Sender);
            PostChirp(sender, text, 0u);
            PoliticsUserMod.Log("AnnounceRepeal: " + text);

            if (!DebugFlags.MinimalChirps)
                PostBillReactionChirps(policy.ToString() + "_Repeal", yes, no);
        }

        /// <summary>
        /// Best-effort refresh of the vanilla City Policies panel so our
        /// SetCityPolicy / UnsetCityPolicy writes show immediately.
        /// Uses the same reflection trick as the Economy refresh.
        /// </summary>
        private static void RefreshCityPoliciesPanel()
        {
            try
            {
                var type = Type.GetType("PoliciesPanel, Assembly-CSharp", false);
                if (type == null) return;
                var obj = UnityEngine.Object.FindObjectOfType(type);
                if (obj == null) return;
                var t = obj.GetType();
                string[] names = new[]
                {
                    "PopulateData", "Populate", "RefreshPanel",
                    "RefreshContent", "Invalidate", "RefreshPolicy"
                };
                foreach (var n in names)
                {
                    var m = t.GetMethod(n,
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic);
                    if (m != null && m.GetParameters().Length == 0)
                    {
                        try { m.Invoke(obj, null); PoliticsUserMod.Log("PoliciesPanel." + n + "() called"); }
                        catch (Exception ex) { PoliticsUserMod.Log("PoliciesPanel." + n + " threw: " + ex.Message); }
                    }
                }
            }
            catch (Exception e)
            {
                PoliticsUserMod.Log("RefreshCityPoliciesPanel failed: " + e.Message);
            }
        }

        public static void AnnounceBill(DistrictPolicies.Policies policy, PoliticsState st)
        {
            var supporters = new HashSet<int>();
            var opponents = new HashSet<int>();
            if (Config.Parties != null)
            {
                for (int i = 0; i < Config.Parties.Length; i++)
                {
                    if (Config.Parties[i] == null) continue;
                    var sup = Config.Parties[i].VanillaPolicies;
                    if (sup != null)
                    {
                        foreach (var vp in sup) if (vp == policy) { supporters.Add(i); break; }
                    }
                    var opp = Config.Parties[i].OpposedPolicies;
                    if (opp != null)
                    {
                        foreach (var vp in opp) if (vp == policy) { opponents.Add(i); break; }
                    }
                }
            }
            if (st.CoalitionPartyIds != null)
            {
                foreach (var cid in st.CoalitionPartyIds)
                {
                    if (Config.Parties != null && cid >= 0 && cid < Config.Parties.Length && !opponents.Contains(cid)) supporters.Add(cid);
                }
            }

            int yes, no, abstain;
            CalculateRealisticBillTally(st, supporters, opponents, true, out yes, out no, out abstain);

            int billNo = st.NextBillNumber++;
            string title = FormatPolicyTitle(policy);
            string abstentionStr = abstain > 0 ? L10n.T(L10nKeys.Chirp_Bill_Abstentions, abstain) : "";
            string text = L10n.T(L10nKeys.Chirp_Bill_Enacted, yes, no, billNo, title, abstentionStr);

            string sender = L10n.T(L10nKeys.Chirp_CityNews_Sender);
            PostChirp(sender, text, 0u);
            PoliticsUserMod.Log("AnnounceBill: " + text);

            if (!DebugFlags.MinimalChirps)
                PostBillReactionChirps(policy.ToString(), yes, no);
        }

        private class BudgetTaxChange
        {
            public string Key;
            public int Delta;
            public bool IsTax;
            public BudgetTaxChange(string key, int delta, bool isTax)
            {
                Key = key;
                Delta = delta;
                IsTax = isTax;
            }
        }

        /// <summary>
        /// Announce a consolidated "budget &amp; tax bill" representing the
        /// coalition's combined fiscal changes. Lists the top 3 changes with
        /// magnitude. Uses realistic vote tallies and L10n.
        /// </summary>
        public static void AnnounceBudgetAndTaxBill(PoliticsState st)
        {
            if (st.CoalitionPartyIds == null || st.CoalitionPartyIds.Count == 0) return;

            // Aggregate deltas across the coalition
            int dRes = 0, dCom = 0, dInd = 0, dOff = 0;
            int dEdu = 0, dHea = 0, dPol = 0, dFire = 0, dElec = 0, dWater = 0, dGar = 0, dTrans = 0, dBeaut = 0, dRoads = 0, dIndustry = 0;
            foreach (var id in st.CoalitionPartyIds)
            {
                if (id < 0 || Config.Parties == null || id >= Config.Parties.Length || Config.Parties[id] == null) continue;
                var m = Config.Parties[id].Modifiers;
                dRes += m.TaxDeltaRes; dCom += m.TaxDeltaCom;
                dInd += m.TaxDeltaInd; dOff += m.TaxDeltaOff;
                dEdu += m.BudgetDeltaEducation;
                dHea += m.BudgetDeltaHealth;
                dPol += m.BudgetDeltaPolice;
                dFire += m.BudgetDeltaFire;
                dElec += m.BudgetDeltaElectricity;
                dWater += m.BudgetDeltaWater;
                dGar += m.BudgetDeltaGarbage;
                dTrans += m.BudgetDeltaTransport;
                dBeaut += m.BudgetDeltaBeautification;
                dRoads += m.BudgetDeltaRoads;
                dIndustry += m.BudgetDeltaIndustry;
            }

            var changes = new List<BudgetTaxChange>();
            AddBudgetTaxChange(changes, "ResTax", dRes, 1, true);
            AddBudgetTaxChange(changes, "ComTax", dCom, 1, true);
            AddBudgetTaxChange(changes, "IndTax", dInd, 1, true);
            AddBudgetTaxChange(changes, "OffTax", dOff, 1, true);
            AddBudgetTaxChange(changes, "EduBudget", dEdu, 5, false);
            AddBudgetTaxChange(changes, "HeaBudget", dHea, 5, false);
            AddBudgetTaxChange(changes, "PolBudget", dPol, 5, false);
            AddBudgetTaxChange(changes, "FireBudget", dFire, 5, false);
            AddBudgetTaxChange(changes, "ElecBudget", dElec, 5, false);
            AddBudgetTaxChange(changes, "WaterBudget", dWater, 5, false);
            AddBudgetTaxChange(changes, "GarBudget", dGar, 5, false);
            AddBudgetTaxChange(changes, "TransBudget", dTrans, 5, false);
            AddBudgetTaxChange(changes, "BeautBudget", dBeaut, 5, false);
            AddBudgetTaxChange(changes, "RoadsBudget", dRoads, 5, false);
            AddBudgetTaxChange(changes, "IndBudget", dIndustry, 5, false);

            if (changes.Count == 0)
            {
                PoliticsUserMod.Log("AnnounceBudgetAndTaxBill: no significant changes, skipping.");
                return;
            }

            changes.Sort((a, b) => Math.Abs(b.Delta).CompareTo(Math.Abs(a.Delta)));
            int top = Math.Min(3, changes.Count);
            var sb = new StringBuilder();
            for (int i = 0; i < top; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(FormatBudgetTaxItem(changes[i].Key, changes[i].Delta, changes[i].IsTax));
            }
            if (changes.Count > top)
            {
                sb.Append(L10n.T(L10nKeys.Chirp_And_More, changes.Count - top));
            }

            var supporters = new HashSet<int>(st.CoalitionPartyIds);
            int yes, no, abstain;
            CalculateRealisticBillTally(st, supporters, null, true, out yes, out no, out abstain);

            int billNo = st.NextBillNumber++;
            string abstentionStr = abstain > 0 ? L10n.T(L10nKeys.Chirp_Bill_Abstentions, abstain) : "";
            string text = L10n.T(L10nKeys.Chirp_Bill_BudgetTax, yes, no, billNo, sb.ToString(), abstentionStr);

            string sender = L10n.T(L10nKeys.Chirp_CityNews_Sender);
            PostChirp(sender, text, 0u);
            PoliticsUserMod.Log("AnnounceBudgetAndTaxBill: " + text);

            if (!DebugFlags.MinimalChirps)
                PostBillReactionChirps("BudgetAndTax", yes, no);
        }

        private static void AddBudgetTaxChange(List<BudgetTaxChange> list, string key, int delta, int threshold, bool isTax)
        {
            if (Math.Abs(delta) >= threshold)
                list.Add(new BudgetTaxChange(key, delta, isTax));
        }

        private static string FormatBudgetTaxItem(string key, int delta, bool isTax)
        {
            bool isKo = L10n.CurrentCode.StartsWith("ko");
            string label;
            if (isKo)
            {
                switch (key)
                {
                    case "ResTax": label = "주거세"; break;
                    case "ComTax": label = "상업세"; break;
                    case "IndTax": label = "공업세"; break;
                    case "OffTax": label = "오피스세"; break;
                    case "EduBudget": label = "교육 예산"; break;
                    case "HeaBudget": label = "보건·의료 예산"; break;
                    case "PolBudget": label = "치안·경찰 예산"; break;
                    case "FireBudget": label = "소방 예산"; break;
                    case "ElecBudget": label = "전력 예산"; break;
                    case "WaterBudget": label = "상하수도 예산"; break;
                    case "GarBudget": label = "폐기물 처리 예산"; break;
                    case "TransBudget": label = "대중교통 예산"; break;
                    case "BeautBudget": label = "공원·환경미화 예산"; break;
                    case "RoadsBudget": label = "도로 유지관리 예산"; break;
                    case "IndBudget": label = "특화산업 예산"; break;
                    default: label = key; break;
                }
            }
            else
            {
                switch (key)
                {
                    case "ResTax": label = "Residential tax"; break;
                    case "ComTax": label = "Commercial tax"; break;
                    case "IndTax": label = "Industrial tax"; break;
                    case "OffTax": label = "Office tax"; break;
                    case "EduBudget": label = "Education budget"; break;
                    case "HeaBudget": label = "Healthcare budget"; break;
                    case "PolBudget": label = "Police budget"; break;
                    case "FireBudget": label = "Fire budget"; break;
                    case "ElecBudget": label = "Electricity budget"; break;
                    case "WaterBudget": label = "Water budget"; break;
                    case "GarBudget": label = "Garbage budget"; break;
                    case "TransBudget": label = "Public Transport budget"; break;
                    case "BeautBudget": label = "Beautification budget"; break;
                    case "RoadsBudget": label = "Roads budget"; break;
                    case "IndBudget": label = "Industry budget"; break;
                    default: label = key; break;
                }
            }

            int mag = Math.Abs(delta);
            if (isTax)
            {
                return delta > 0
                    ? L10n.T(L10nKeys.Chirp_Tax_Raise, label, mag)
                    : L10n.T(L10nKeys.Chirp_Tax_Cut, label, mag);
            }
            else
            {
                return delta > 0
                    ? L10n.T(L10nKeys.Chirp_Budget_Raise, label, mag)
                    : L10n.T(L10nKeys.Chirp_Budget_Cut, label, mag);
            }
        }

        private static string SplitCamelCase(string raw)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < raw.Length; i++)
            {
                if (i > 0 && char.IsUpper(raw[i])) sb.Append(' ');
                sb.Append(raw[i]);
            }
            return sb.ToString();
        }

        /// <summary>Turn a PascalCase policy enum name into a human-readable title.</summary>
        private static string FormatPolicyTitle(DistrictPolicies.Policies policy)
        {
            bool isKo = L10n.CurrentCode.StartsWith("ko");
            string raw = policy.ToString();
            if (isKo)
            {
                switch (raw)
                {
                    case "FreeTransport": return "대중교통 무료화법";
                    case "Recycling": return "자원 재활용 의무화법";
                    case "SmokeDetectors": return "화재 감지기 설치 의무화법";
                    case "EducationBoost": return "교육 예산 특별 지원법";
                    case "ExtraInsulation": return "주택 단열 및 에너지 개선법";
                    case "BigBusiness": return "대기업 육성 및 유치 지원법";
                    case "HighTechHousing": return "첨단 주거단지 조성법";
                    case "DoubleTime": return "근로자 초과근무 수당 2배 지급법";
                    case "NoHeavy":
                    case "HeavyTrafficBan": return "대형 화물차 도심 통행 제한법";
                    case "OnlyAtNight": return "야간 배송 및 영업 한정 허용법";
                    case "OldTown": return "구도심 역사문화 보존법";
                    case "EncourageBiking": return "자전거 이용 활성화법";
                    case "PowerSaving": return "절전 대책 촉진법";
                    case "WaterSaving": return "절수 기기 보급법";
                    case "ParksAndRecreation": return "공원 및 여가 공간 진흥법";
                    case "PetBan": return "반려동물 사육 제한법";
                    case "SmokingBan": return "공공장소 금연법";
                    case "IndustrialSpacePlanning": return "산업 단지 입지 계획법";
                    default: return SplitCamelCase(raw) + " 조례안";
                }
            }
            else
            {
                switch (raw)
                {
                    case "FreeTransport": return "provide Free Public Transport";
                    case "Recycling": return "mandate City-Wide Recycling";
                    case "SmokeDetectors": return "require Smoke Detectors";
                    case "EducationBoost": return "fund an Education Boost";
                    case "ExtraInsulation": return "subsidize Home Insulation";
                    case "BigBusiness": return "support Big Business Benefits";
                    case "HighTechHousing": return "incentivize High-Tech Housing";
                    case "DoubleTime": return "enforce Double Time wages";
                    case "NoHeavy": return "ban Heavy Traffic downtown";
                    case "OnlyAtNight": return "permit Night-Only Operations";
                    case "OldTown": return "protect the Old Town Heritage";
                    case "HeavyTrafficBan": return "ban Heavy Traffic citywide";
                    default: return "enact " + SplitCamelCase(raw);
                }
            }
        }

        private static void ApplyCustomModifiers(int sign)
        {
            var st = PoliticsState.Instance;
            if (st.CoalitionPartyIds == null || st.CoalitionPartyIds.Count == 0)
            {
                PoliticsUserMod.Log("ApplyCustomModifiers: no coalition, skipping (sign=" + sign + ")");
                return;
            }
            PoliticsUserMod.Log("ApplyCustomModifiers sign=" + sign +
                ", coalition=" + st.CoalitionPartyIds.Count + " parties");

            // Aggregate modifier deltas across coalition parties.
            int dRes = 0, dCom = 0, dInd = 0, dOff = 0;
            int dEdu = 0, dHea = 0, dPol = 0, dFire = 0, dElec = 0, dWater = 0, dGar = 0, dTrans = 0, dBeaut = 0, dRoads = 0, dIndustry = 0;
            foreach (var id in st.CoalitionPartyIds)
            {
                if (id < 0 || Config.Parties == null || id >= Config.Parties.Length || Config.Parties[id] == null) continue;
                var m = Config.Parties[id].Modifiers;
                dRes += m.TaxDeltaRes; dCom += m.TaxDeltaCom;
                dInd += m.TaxDeltaInd; dOff += m.TaxDeltaOff;
                dEdu += m.BudgetDeltaEducation;
                dHea += m.BudgetDeltaHealth;
                dPol += m.BudgetDeltaPolice;
                dFire += m.BudgetDeltaFire;
                dElec += m.BudgetDeltaElectricity;
                dWater += m.BudgetDeltaWater;
                dGar += m.BudgetDeltaGarbage;
                dTrans += m.BudgetDeltaTransport;
                dBeaut += m.BudgetDeltaBeautification;
                dRoads += m.BudgetDeltaRoads;
                dIndustry += m.BudgetDeltaIndustry;
            }

            var em = Singleton<EconomyManager>.instance;
            try
            {
                // Taxes
                AdjustTax(em, ItemClass.Service.Residential, ItemClass.SubService.ResidentialLow, dRes * sign);
                AdjustTax(em, ItemClass.Service.Residential, ItemClass.SubService.ResidentialHigh, dRes * sign);
                AdjustTax(em, ItemClass.Service.Commercial, ItemClass.SubService.CommercialLow, dCom * sign);
                AdjustTax(em, ItemClass.Service.Commercial, ItemClass.SubService.CommercialHigh, dCom * sign);
                AdjustTax(em, ItemClass.Service.Industrial, ItemClass.SubService.None, dInd * sign);
                AdjustTax(em, ItemClass.Service.Office, ItemClass.SubService.None, dOff * sign);

                // Budgets (full city services coverage)
                AdjustBudget(em, ItemClass.Service.Education, dEdu * sign);
                AdjustBudget(em, ItemClass.Service.HealthCare, dHea * sign);
                AdjustBudget(em, ItemClass.Service.PoliceDepartment, dPol * sign);
                AdjustBudget(em, ItemClass.Service.FireDepartment, dFire * sign);
                AdjustBudget(em, ItemClass.Service.Electricity, dElec * sign);
                AdjustBudget(em, ItemClass.Service.Water, dWater * sign);
                AdjustBudget(em, ItemClass.Service.Garbage, dGar * sign);
                AdjustBudget(em, ItemClass.Service.PublicTransport, dTrans * sign);
                AdjustBudget(em, ItemClass.Service.Beautification, dBeaut * sign);
                AdjustBudget(em, ItemClass.Service.Road, dRoads * sign);
                AdjustBudget(em, ItemClass.Service.Industrial, dIndustry * sign);
            }
            catch (Exception e)
            {
                PoliticsUserMod.Log("Failed to adjust economy: " + e.Message);
            }
        }

        private static void AdjustTax(EconomyManager em, ItemClass.Service svc, ItemClass.SubService sub, int delta)
        {
            if (delta == 0) return;
            try
            {
                int dayCur = em.GetTaxRate(svc, sub, ItemClass.Level.None);
                int newRate = Mathf.Clamp(dayCur + delta, 1, 29);
                var capSvc = svc; var capSub = sub; var capRate = newRate;
                Singleton<SimulationManager>.instance.AddAction(() =>
                {
                    try
                    {
                        Singleton<EconomyManager>.instance.SetTaxRate(capSvc, capSub, ItemClass.Level.None, capRate);
                        int verify = Singleton<EconomyManager>.instance.GetTaxRate(capSvc, capSub, ItemClass.Level.None);
                        PoliticsUserMod.Log("SimWrite tax " + capSvc + "/" + capSub + ": target=" + capRate + ", readback=" + verify +
                                            (verify == capRate ? " OK" : " MISMATCH"));
                    }
                    catch (Exception ex) { PoliticsUserMod.Log("SetTaxRate(" + capSvc + "/" + capSub + ") on sim thread: " + ex.Message); }
                });
                PoliticsUserMod.Log("Queued tax " + svc + "/" + sub + ": " + dayCur + " -> " + newRate + " (delta " + delta + ")");
            }
            catch (Exception e)
            {
                PoliticsUserMod.Log("AdjustTax " + svc + "/" + sub + " failed: " + e.Message);
            }
        }

        private static void AdjustBudget(EconomyManager em, ItemClass.Service svc, int delta)
        {
            if (delta == 0) return;
            try
            {
                int dayCur = em.GetBudget(svc, ItemClass.SubService.None, false);
                int newDay = Mathf.Clamp(dayCur + delta, 50, 150);
                int nightCur = em.GetBudget(svc, ItemClass.SubService.None, true);
                int newNight = Mathf.Clamp(nightCur + delta, 50, 150);
                var capSvc = svc; var capDay = newDay; var capNight = newNight;
                Singleton<SimulationManager>.instance.AddAction(() =>
                {
                    try
                    {
                        Singleton<EconomyManager>.instance.SetBudget(capSvc, ItemClass.SubService.None, capDay, false);
                        Singleton<EconomyManager>.instance.SetBudget(capSvc, ItemClass.SubService.None, capNight, true);
                        int vd = Singleton<EconomyManager>.instance.GetBudget(capSvc, ItemClass.SubService.None, false);
                        int vn = Singleton<EconomyManager>.instance.GetBudget(capSvc, ItemClass.SubService.None, true);
                        PoliticsUserMod.Log("SimWrite budget " + capSvc + ": dayTgt=" + capDay + " read=" + vd + ", nightTgt=" + capNight + " read=" + vn +
                                            (vd == capDay && vn == capNight ? " OK" : " MISMATCH"));
                    }
                    catch (Exception ex) { PoliticsUserMod.Log("SetBudget(" + capSvc + ") on sim thread: " + ex.Message); }
                });
                PoliticsUserMod.Log("Queued budget " + svc + ": day " + dayCur + "->" + newDay + ", night " + nightCur + "->" + newNight + " (delta " + delta + ")");
            }
            catch (Exception e)
            {
                PoliticsUserMod.Log("AdjustBudget " + svc + " failed: " + e.Message);
            }
        }

        // ---- Notifications -----------------------------------------------

        public static void ShowToast(string msg)
        {
            PoliticsUserMod.Log("TOAST: " + msg);
            PoliticsPanel.LatestToast = msg;
            PoliticsPanel.LatestToastTime = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Post a chirp (in-game Twitter-style notification) from a given sender.
        ///
        /// </summary>
        public static void PostChirp(string sender, string text, uint senderSeed = 0u)
        {
            try
            {
                var panel = ChirpPanel.instance;
                if (panel == null) return; // menu / loading - silently skip

                if (senderSeed == 0u)
                {
                    // Use a hash of the sender name so repeated chirps from
                    // the same party cluster in the chirper.
                    senderSeed = (uint)(Math.Abs((sender ?? "").GetHashCode()) | 1);
                }

                panel.AddMessage(new PoliticsTransientChirp(sender, text, senderSeed));
            }
            catch (Exception e)
            {
                PoliticsUserMod.Log("PostChirp failed: " + e.Message);
            }
        }

        /// <summary>
        /// Pick a random created (non-dummy) citizen and return their display
        /// name. Returns "Concerned Citizen" if lookup fails.
        /// </summary>
        public static string PickRandomCitizenName(out uint citizenId)
        {
            citizenId = 0u;
            try
            {
                var cm = Singleton<CitizenManager>.instance;
                if (cm == null || cm.m_citizens.m_buffer == null) return "Concerned Citizen";
                uint size = (uint)Math.Min((int)cm.m_citizens.m_size, cm.m_citizens.m_buffer.Length);
                if (size <= 1) return "Concerned Citizen";
                for (int tries = 0; tries < 30; tries++)
                {
                    uint idx = (uint)_rng.Next(1, (int)size);
                    if (idx >= cm.m_citizens.m_buffer.Length) continue;
                    var c = cm.m_citizens.m_buffer[idx];
                    if ((c.m_flags & Citizen.Flags.Created) == 0) continue;
                    if ((c.m_flags & Citizen.Flags.DummyTraffic) != 0) continue;
                    // Children/teens usually don't chirp about politics
                    var g = Citizen.GetAgeGroup(c.m_age);
                    if (g == Citizen.AgeGroup.Child || g == Citizen.AgeGroup.Teen) continue;
                    citizenId = idx;
                    string name = cm.GetCitizenName(idx);
                    if (string.IsNullOrEmpty(name)) continue;
                    return name;
                }
            }
            catch { }
            return "Concerned Citizen";
        }

        /// <summary>Post one right-wing-leaning citizen chirp about the deficit.</summary>
        public static void PostRandomCitizenDeficitChirp()
        {
            // Pick a right-wing party for the hashtag.
            int rightPartyId = -1;
            float bestX = float.MinValue;
            if (Config.Parties != null)
            {
                for (int i = 0; i < Config.Parties.Length; i++)
                {
                    if (Config.Parties[i] != null && Config.Parties[i].Ideology.x > bestX)
                    {
                        bestX = Config.Parties[i].Ideology.x;
                        rightPartyId = i;
                    }
                }
            }
            string tag = (Config.Parties != null && rightPartyId >= 0 && rightPartyId < Config.Parties.Length && Config.Parties[rightPartyId] != null)
                ? "#" + Config.Parties[rightPartyId].ShortName : "";

            bool isKo = L10n.CurrentCode.StartsWith("ko");
            string[] pool;
            if (isKo)
            {
                pool = new[]
                {
                    "세금이 너무 과합니다. 정권 교체가 답이다. " + tag,
                    "도시 재정이 또 적자라니... 살림살이 진짜 못하네. 다음엔 보수 정당 찍는다.",
                    "세금은 왕창 걷어가면서 도대체 어디다 쓰는 건지? " + tag,
                    "가계부도 이렇게 쓰면 파산합니다. 낭비성 예산 당장 삭감하라! " + tag,
                    "적자 도시 만들고 세금으로 메우려 하지 마라. 다음 선거 때 표로 심판하겠다.",
                    "기업 다 떠나고 세금만 늘어나네. 경제 살릴 정당이 필요하다. " + tag,
                    "내 피 같은 세금 좀 그만 낭비해라 진짜... " + tag,
                    "허리띠 졸라매야 할 판에 선심성 예산만 펑펑 쓰네. " + tag,
                };
            }
            else
            {
                pool = new[]
                {
                    "My taxes are too high. Time for a change. " + tag,
                    "This government can't balance a budget. Voting right next time.",
                    "We need lower taxes and more jobs. " + tag,
                    "Running a city is like running a business! cut the waste.",
                    "Another deficit? I'm done funding this. " + tag,
                    "Big spenders are bankrupting us. Switching my vote.",
                    "If my household ran like this, we'd be homeless.",
                    "Austerity now! " + tag,
                    "Liberez-nous des liberaux tabarnak!!! " + tag,
                    "Baissez les taxes tabarnak, on est plus capable.",
                    "Osti que ca me tanne, toujours plus d'impots. " + tag,
                    "Ben voyons donc, j'en paye-tu assez d'impots?! " + tag,
                    "Maudites taxes, on a rien pantoute pour notre argent " + tag,
                    "Calisse, arretez de depenser mon argent. " + tag,
                };
            }

            uint cid;
            string name = PickRandomCitizenName(out cid);
            if (cid == 0u) return; // no real citizen found - skip; clicking a fake ID does nothing
            string msg = pool[_rng.Next(0, pool.Length)];
            PostChirp(name, msg, cid);
        }

        /// <summary>
        /// Post 1-2 short citizen reactions after a bill passes. Mix of
        /// supportive and opposed, weighted toward the majority side of the vote.
        /// </summary>
        public static void PostBillReactionChirps(string billKey, int yes, int no)
        {
            int total = Math.Max(1, yes + no);
            float supportShare = yes / (float)total;
            int count = _rng.NextDouble() < 0.25 ? 1 : 0;
            if (count == 0) return;

            bool isKo = L10n.CurrentCode.StartsWith("ko");
            string[] supportive;
            string[] opposed;

            if (isKo)
            {
                supportive = new[]
                {
                    "드디어 통과됐네! 진작 이렇게 했어야지. #환영",
                    "시의회가 제대로 일하네요. 우리 동네도 살기 좋아질 듯!",
                    "이번 법안 정말 마음에 든다. 투표한 보람이 있네. #시민승리",
                    "상식이 통하는 도시가 되어가고 있네요. 응원합니다.",
                    "우리 아이들을 위해서라도 꼭 필요했던 법안!",
                    "변화를 체감할 수 있는 좋은 정책입니다. 지지합니다.",
                };
                opposed = new[]
                {
                    "또 시민 혈세 낭비하네... 우리 의견은 안 듣나?",
                    "선거 공약이랑 딴판이네. 다음 선거 때 두고 보자. #실망",
                    "도시 우선순위가 완전히 잘못됐다. 답답하네.",
                    "탁상행정의 전형적인 표본... 현장 목소리 좀 듣길.",
                    "세금만 오르고 실효성은 하나도 없는 엉터리 법안.",
                    "주민들한테 전혀 도움 안 되는 생색내기용 법안이다.",
                };
            }
            else
            {
                supportive = new[]
                {
                    "Finally! This was long overdue. #Win",
                    "Great move by parliament today. Makes a real difference.",
                    "I actually feel represented for once.",
                    "Common sense wins. Well done.",
                    "This is why I voted. Keep it going.",
                    "Seeing real change in my neighborhood.",
                };
                opposed = new[]
                {
                    "Waste of money. They don't listen to us.",
                    "So much for campaign promises. I'm done.",
                    "Wrong priorities. Again.",
                    "Wait until next election. We remember.",
                    "Taxpayer funded nonsense.",
                    "This bill helps no one I know.",
                };
            }

            for (int i = 0; i < count; i++)
            {
                bool isSupport = _rng.NextDouble() < supportShare;
                var pool = isSupport ? supportive : opposed;
                string msg = pool[_rng.Next(0, pool.Length)];
                uint cid;
                string name = PickRandomCitizenName(out cid);
                if (cid == 0u) continue; // need a real citizen ID for the chirp to be clickable
                PostChirp(name, msg, cid);
            }
        }

        public static void ShowResultsPopup(ElectionResult r)
        {
            if (r == null || r.SeatsByParty == null) return;
            bool isKo = L10n.CurrentCode.StartsWith("ko");
            var sb = new StringBuilder();
            if (isKo)
                sb.AppendLine("=== 제" + r.Year + "년 " + r.Month + "월 선거 결과 ===");
            else
                sb.AppendLine("=== Election " + r.Year + "-" + r.Month + " ===");

            int count = Math.Min(r.SeatsByParty.Length, Config.Parties != null ? Config.Parties.Length : 0);
            for (int i = 0; i < count; i++)
            {
                if (Config.Parties[i] == null) continue;
                string seatLabel = isKo ? "석" : "seats";
                float voteShare = (r.VoteShareByParty != null && i < r.VoteShareByParty.Length) ? r.VoteShareByParty[i] : 0f;
                sb.AppendLine(string.Format("  {0,-25} {1,3} {2}  ({3:P1})",
                    Config.Parties[i].FullName, r.SeatsByParty[i], seatLabel, voteShare));
            }
            string turnoutLabel = isKo ? "투표율: " : "Turnout: ";
            sb.Append(turnoutLabel + (int)(r.Turnout * 100) + "%");
            PoliticsPanel.ResultsPopupText = sb.ToString();
            PoliticsPanel.ResultsPopupShownUntil = Time.realtimeSinceStartup + 15f;
            PoliticsUserMod.Log(sb.ToString());
        }
    }
}
