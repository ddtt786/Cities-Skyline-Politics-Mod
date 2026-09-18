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

namespace PoliticsMod
{


    /// <summary>
    /// Harmony prefix on BuildingAI.GetColor. When the overlay is active and
    /// the building is residential, returns our custom color and skips the
    /// original method.
    /// </summary>
    [HarmonyPatch(typeof(BuildingAI), "GetColor")]
    public static class BuildingAI_GetColor_Patch
    {
        public static bool Prefix(BuildingAI __instance, ushort buildingID, ref Building data,
                                  InfoManager.InfoMode infoMode, InfoManager.SubInfoMode subInfoMode, ref Color __result)
        {
            try
            {
                var st = PoliticsState.Instance;
                if (st == null || !st.Initialized) return true;
                if (st.Overlay == OverlayMode.Off) return true;

                // When our overlay is active, tint buildings in Density info view mode
                // as well as in normal game view (InfoMode.None). Any other info mode: leave alone.
                if (infoMode != InfoManager.InfoMode.Density && infoMode != InfoManager.InfoMode.None) return true;

                // Only color residential buildings (matches our data coverage).
                var info = data.Info;
                if (info == null) return true;
                if (info.GetService() != ItemClass.Service.Residential) return true;

                // Neutral "no data" color for residential buildings that have no
                // voter data yet (built after last election, not sampled, etc.).
                Color noData = new Color(0.35f, 0.35f, 0.4f, 1f);

                Color c;
                bool show;
                switch (st.Overlay)
                {
                    case OverlayMode.Party:
                        {
                            if (st.DominantPartyByBuilding == null || buildingID >= st.DominantPartyByBuilding.Length)
                                return true;
                            byte pid = st.DominantPartyByBuilding[buildingID];
                            var parties = Config.Parties;
                            if (parties == null || pid >= parties.Length || pid >= PartyCountRef.Value)
                            {
                                __result = noData;
                                return false; // no data - show neutral
                            }
                            c = (Color)parties[pid].Color;
                            show = true;
                            break;
                        }
                    case OverlayMode.Turnout:
                        {
                            if (st.TurnoutByBuilding == null || buildingID >= st.TurnoutByBuilding.Length)
                                return true;
                            byte t = st.TurnoutByBuilding[buildingID];
                            if (t == 0) { __result = noData; return false; }
                            c = Color.Lerp(new Color(0.7f, 0.1f, 0.1f), new Color(0.1f, 0.8f, 0.1f), t / 100f);
                            show = true;
                            break;
                        }
                    case OverlayMode.Satisfaction:
                        {
                            if (st.SatisfactionByBuilding == null || buildingID >= st.SatisfactionByBuilding.Length)
                                return true;
                            byte sa = st.SatisfactionByBuilding[buildingID];
                            if (sa == 0) { __result = noData; return false; }
                            c = Color.Lerp(new Color(0.7f, 0.1f, 0.1f), new Color(0.2f, 0.7f, 0.9f), sa / 100f);
                            show = true;
                            break;
                        }
                    default:
                        return true;
                }

                if (!show) return true;

                // Boost saturation/brightness so it reads well on residential geometry.
                c.a = 1f;
                __result = c;
                return false; // skip original
            }
            catch
            {
                return true; // on any exception, fall back to vanilla game rendering without crashing!
            }
        }
    }
}
