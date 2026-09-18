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
using PoliticsMod.Localization;
using UnityEngine;

namespace PoliticsMod
{
    // ========================================================================
    //  ELECTION STATS PANEL - detailed election statistics, history browser,
    //  district-level breakdown, Senate results, and demographic hover tooltips.
    // ========================================================================
    public class ElectionStatsPanel : UIPanel
    {
        private static ElectionStatsPanel _instance;

        public static void Toggle()
        {
            var view = UIView.GetAView();
            if (view == null) return;
            bool justCreated = false;
            if (_instance == null)
            {
                _instance = view.AddUIComponent(typeof(ElectionStatsPanel)) as ElectionStatsPanel;
                justCreated = true;
            }

            if (_instance != null)
            {
                _instance.isVisible = justCreated ? true : !_instance.isVisible;
                if (_instance.isVisible) _instance.Refresh();
            }
        }

        private UILabel _title;
        private UIButton _closeBtn;

        // History and District dropdowns
        private UILabel _electionLabel;
        private UIDropDown _electionDropdown;
        private UILabel _districtLabel;
        private UIDropDown _districtDropdown;

        private UILabel _subtitle;
        private UIScrollablePanel _scrollBody;
        private UIScrollbar _scrollBar;
        private UIScrollablePanel _chartPanel;

        private int _selectedHistoryIndex = 0;   // 0 = latest election
        private int _selectedDistrictIndex = 0;  // 0 = City-Wide

        public override void Start()
        {
            base.Start();
            width = 860;
            height = 820;
            backgroundSprite = "MenuPanel2";
            canFocus = true;
            isInteractive = true;
            clipChildren = false;
            relativePosition = new Vector3(100, 30);
            BuildUI();
            Refresh();
            L10n.LanguageChanged += OnLanguageChanged;
        }

        public override void OnDestroy()
        {
            L10n.LanguageChanged -= OnLanguageChanged;
            base.OnDestroy();
        }

        private void OnLanguageChanged()
        {
            if (_title != null) _title.text = L10n.T(L10nKeys.Stats_Title);
            if (_closeBtn != null) _closeBtn.text = L10n.T(L10nKeys.Common_CloseX);
            if (_electionLabel != null) _electionLabel.text = L10n.T(L10nKeys.Stats_Election_Label);
            if (_districtLabel != null) _districtLabel.text = L10n.T(L10nKeys.Stats_District_Label);
            Refresh();
        }

        private UIDropDown CreateDropDown(Vector2 pos, Vector2 sz)
        {
            var dd = AddUIComponent<UIDropDown>();
            dd.size = sz;
            dd.relativePosition = pos;
            dd.listBackground = "GenericPanelLight";
            dd.itemHeight = 24;
            dd.itemHover = "ListItemHover";
            dd.itemHighlight = "ListItemHighlight";
            dd.normalBgSprite = "ButtonMenu";
            dd.hoveredBgSprite = "ButtonMenuHovered";
            dd.focusedBgSprite = "ButtonMenu";
            dd.disabledBgSprite = "ButtonMenuDisabled";
            dd.autoListWidth = true;
            dd.listHeight = 260;
            dd.listPosition = UIDropDown.PopupListPosition.Below;
            dd.clampListToScreen = true;
            dd.foregroundSpriteMode = UIForegroundSpriteMode.Stretch;
            dd.popupColor = new Color32(45, 52, 61, 255);
            dd.popupTextColor = new Color32(220, 220, 220, 255);
            dd.textScale = 0.8f;
            dd.textFieldPadding = new RectOffset(8, 20, 5, 5);
            dd.itemPadding = new RectOffset(8, 8, 4, 4);
            dd.isInteractive = true;
            dd.canFocus = true;
            dd.triggerButton = dd;

            // Ensure dropdown is brought to front on click
            dd.eventClick += (c, p) =>
            {
                dd.BringToFront();
            };

            // Scrollbar for the popup list
            var ddScrollbar = dd.AddUIComponent<UIScrollbar>();
            ddScrollbar.width = 10f;
            ddScrollbar.height = dd.listHeight;
            ddScrollbar.orientation = UIOrientation.Vertical;
            ddScrollbar.stepSize = 24f;
            ddScrollbar.incrementAmount = 48f;
            var ddTrack = ddScrollbar.AddUIComponent<UISlicedSprite>();
            ddTrack.relativePosition = Vector3.zero;
            ddTrack.size = ddScrollbar.size;
            ddTrack.spriteName = "ScrollbarTrack";
            ddScrollbar.trackObject = ddTrack;
            var ddThumb = ddTrack.AddUIComponent<UISlicedSprite>();
            ddThumb.relativePosition = Vector3.zero;
            ddThumb.size = new Vector2(10f, 30f);
            ddThumb.spriteName = "ScrollbarThumb";
            ddScrollbar.thumbObject = ddThumb;
            dd.listScrollbar = ddScrollbar;

            // Dropdown arrow indicator
            var arrow = dd.AddUIComponent<UISprite>();
            arrow.spriteName = "IconDownArrow";
            arrow.size = new Vector2(12f, 12f);
            arrow.relativePosition = new Vector3(sz.x - 18f, (sz.y - 12f) / 2f);
            arrow.isInteractive = false;

            return dd;
        }

        private UIButton CreateNavButton(Vector2 pos, string txt, Action onClick)
        {
            var btn = AddUIComponent<UIButton>();
            btn.size = new Vector2(24, 26);
            btn.relativePosition = pos;
            btn.text = txt;
            btn.textScale = 0.85f;
            btn.normalBgSprite = "ButtonMenu";
            btn.hoveredBgSprite = "ButtonMenuHovered";
            btn.pressedBgSprite = "ButtonMenuPressed";
            btn.eventClick += (c, p) => onClick();
            return btn;
        }

        private void BuildUI()
        {
            _title = AddUIComponent<UILabel>();
            _title.text = L10n.T(L10nKeys.Stats_Title);
            _title.textScale = 1.15f;
            _title.relativePosition = new Vector3(15, 12);

            UIHelpers.MakeDraggable(this);

            var close = AddUIComponent<UIButton>();
            close.text = L10n.T(L10nKeys.Common_CloseX);
            close.size = new Vector2(28, 24);
            close.relativePosition = new Vector3(width - 35, 8);
            close.normalBgSprite = "ButtonMenu";
            close.hoveredBgSprite = "ButtonMenuHovered";
            close.pressedBgSprite = "ButtonMenuPressed";
            close.eventClick += (c, p) => { isVisible = false; };
            _closeBtn = close;

            // Row 2: Election and District dropdowns with prev/next navigation
            _electionLabel = AddUIComponent<UILabel>();
            _electionLabel.text = L10n.T(L10nKeys.Stats_Election_Label);
            _electionLabel.textScale = 0.85f;
            _electionLabel.relativePosition = new Vector3(15, 42);

            CreateNavButton(new Vector2(85, 38), "<", () =>
            {
                if (_electionDropdown != null && _electionDropdown.items != null && _electionDropdown.items.Length > 0)
                {
                    int next = _electionDropdown.selectedIndex - 1;
                    if (next >= 0) _electionDropdown.selectedIndex = next;
                }
            });

            _electionDropdown = CreateDropDown(new Vector2(113, 38), new Vector2(210, 26));
            _electionDropdown.eventSelectedIndexChanged += (c, idx) =>
            {
                if (_selectedHistoryIndex != idx)
                {
                    _selectedHistoryIndex = idx;
                    _selectedDistrictIndex = 0; // Reset district selection when changing election
                    UpdateDistrictDropdown();
                    RefreshCharts();
                }
            };

            CreateNavButton(new Vector2(327, 38), ">", () =>
            {
                if (_electionDropdown != null && _electionDropdown.items != null && _electionDropdown.items.Length > 0)
                {
                    int next = _electionDropdown.selectedIndex + 1;
                    if (next < _electionDropdown.items.Length) _electionDropdown.selectedIndex = next;
                }
            });

            _districtLabel = AddUIComponent<UILabel>();
            _districtLabel.text = L10n.T(L10nKeys.Stats_District_Label);
            _districtLabel.textScale = 0.85f;
            _districtLabel.relativePosition = new Vector3(365, 42);

            CreateNavButton(new Vector2(430, 38), "<", () =>
            {
                if (_districtDropdown != null && _districtDropdown.items != null && _districtDropdown.items.Length > 0)
                {
                    int next = _districtDropdown.selectedIndex - 1;
                    if (next >= 0) _districtDropdown.selectedIndex = next;
                }
            });

            _districtDropdown = CreateDropDown(new Vector2(458, 38), new Vector2(280, 26));
            _districtDropdown.eventSelectedIndexChanged += (c, idx) =>
            {
                if (_selectedDistrictIndex != idx)
                {
                    _selectedDistrictIndex = idx;
                    RefreshCharts();
                }
            };

            CreateNavButton(new Vector2(742, 38), ">", () =>
            {
                if (_districtDropdown != null && _districtDropdown.items != null && _districtDropdown.items.Length > 0)
                {
                    int next = _districtDropdown.selectedIndex + 1;
                    if (next < _districtDropdown.items.Length) _districtDropdown.selectedIndex = next;
                }
            });

            // Ensure dropdown popups render on top
            _electionDropdown.BringToFront();
            _districtDropdown.BringToFront();

            // Row 3: Subtitle
            _subtitle = AddUIComponent<UILabel>();
            _subtitle.textScale = 0.85f;
            _subtitle.relativePosition = new Vector3(15, 72);
            _subtitle.textColor = new Color32(200, 200, 210, 255);

            // Scrollable chart area
            _scrollBody = AddUIComponent<UIScrollablePanel>();
            _scrollBody.relativePosition = new Vector3(15, 98);
            _scrollBody.size = new Vector2(width - 45, height - 110);
            _scrollBody.autoLayout = false;
            _scrollBody.clipChildren = true;
            _scrollBody.scrollWheelDirection = UIOrientation.Vertical;
            _scrollBody.builtinKeyNavigation = true;

            _scrollBar = AddUIComponent<UIScrollbar>();
            _scrollBar.relativePosition = new Vector3(width - 27, 98);
            _scrollBar.size = new Vector2(12, height - 110);
            _scrollBar.orientation = UIOrientation.Vertical;
            _scrollBar.stepSize = 20f;
            _scrollBar.incrementAmount = 40f;
            var sbTrack = _scrollBar.AddUIComponent<UISlicedSprite>();
            sbTrack.relativePosition = Vector3.zero;
            sbTrack.size = _scrollBar.size;
            sbTrack.spriteName = "ScrollbarTrack";
            _scrollBar.trackObject = sbTrack;
            var sbThumb = sbTrack.AddUIComponent<UISlicedSprite>();
            sbThumb.relativePosition = Vector3.zero;
            sbThumb.spriteName = "ScrollbarThumb";
            sbThumb.size = new Vector2(12, 40);
            _scrollBar.thumbObject = sbThumb;
            _scrollBody.verticalScrollbar = _scrollBar;
            _scrollBody.eventMouseWheel += (c, e) =>
            {
                _scrollBody.scrollPosition = new Vector2(
                    _scrollBody.scrollPosition.x,
                    Mathf.Max(0f, _scrollBody.scrollPosition.y - e.wheelDelta * 40f));
            };

            _chartPanel = _scrollBody;
        }

        private ElectionResult GetSelectedResult()
        {
            var st = PoliticsState.Instance;
            if (st == null) return null;
            if (st.History != null && st.History.Count > 0)
            {
                int idx = (st.History.Count - 1) - _selectedHistoryIndex;
                if (idx >= 0 && idx < st.History.Count) return st.History[idx];
                return st.History[st.History.Count - 1];
            }
            return st.LastResult;
        }

        private DistrictResult GetSelectedDistrict(ElectionResult r)
        {
            if (r == null || r.DistrictResults == null || _selectedDistrictIndex <= 0)
                return null;
            int dIdx = _selectedDistrictIndex - 1;
            if (dIdx >= 0 && dIdx < r.DistrictResults.Count)
                return r.DistrictResults[dIdx];
            return null;
        }

        private void UpdateDistrictDropdown()
        {
            if (_districtDropdown == null) return;
            var r = GetSelectedResult();
            var items = new List<string>();
            items.Add(L10n.T(L10nKeys.Stats_District_CityWide));

            if (r != null && r.DistrictResults != null && r.DistrictResults.Count > 0)
            {
                foreach (var dr in r.DistrictResults)
                {
                    items.Add(string.Format("{0} ({1})", dr.DistrictName, L10n.T(L10nKeys.Stats_Votes_Suffix, dr.TotalVotes)));
                }
            }

            _districtDropdown.items = items.ToArray();
            if (_selectedDistrictIndex >= items.Count) _selectedDistrictIndex = 0;
            _districtDropdown.selectedIndex = _selectedDistrictIndex;
        }

        public void Refresh()
        {
            var st = PoliticsState.Instance;
            if (_electionDropdown != null)
            {
                var electionItems = new List<string>();
                if (st != null && st.History != null && st.History.Count > 0)
                {
                    for (int i = st.History.Count - 1; i >= 0; i--)
                    {
                        var res = st.History[i];
                        electionItems.Add(string.Format(L10n.T(L10nKeys.Stats_Election_Item), i + 1, res.Year, res.Month));
                    }
                }
                else
                {
                    electionItems.Add(L10n.T(L10nKeys.Stats_NoData_Subtitle));
                }
                _electionDropdown.items = electionItems.ToArray();
                if (_selectedHistoryIndex >= electionItems.Count) _selectedHistoryIndex = 0;
                _electionDropdown.selectedIndex = _selectedHistoryIndex;
            }

            UpdateDistrictDropdown();
            RefreshCharts();
        }

        private void RefreshCharts()
        {
            if (_chartPanel == null) return;
            var kids = new List<GameObject>();
            foreach (Transform t in _chartPanel.transform) kids.Add(t.gameObject);
            foreach (var g in kids) UnityEngine.Object.Destroy(g);

            var st = PoliticsState.Instance;
            var r = GetSelectedResult();
            if (st == null || r == null)
            {
                _subtitle.text = L10n.T(L10nKeys.Stats_NoData_Subtitle);
                var msg = _chartPanel.AddUIComponent<UILabel>();
                msg.text = L10n.T(L10nKeys.Stats_NoData_Body);
                msg.textScale = 0.9f;
                msg.autoSize = false;
                msg.size = new Vector2(_chartPanel.width - 20f, 180f);
                msg.relativePosition = new Vector3(10f, 40f);
                msg.wordWrap = true;
                msg.textColor = new Color32(220, 220, 225, 255);
                return;
            }

            var dr = GetSelectedDistrict(r);
            int total = 0;
            int[] tally;
            int[,] ageData;
            int[,] eduData;
            int[,] wealthData;

            if (dr != null)
            {
                total = dr.TotalVotes;
                _subtitle.text = string.Format("{0} - [{1}] {2}",
                    string.Format(L10n.T(L10nKeys.Stats_Election_Item), (_selectedHistoryIndex >= 0 && st.History != null ? (st.History.Count - _selectedHistoryIndex) : 1), r.Year, r.Month),
                    dr.DistrictName,
                    L10n.T(L10nKeys.Stats_Votes_Suffix, total));
                tally = dr.VotesByGrievance ?? new int[9];
                ageData = dr.VotesByAgeParty;
                eduData = dr.VotesByEduParty;
                wealthData = dr.VotesByWealthParty;
            }
            else
            {
                tally = r.VotesByGrievance ?? new int[9];
                for (int i = 0; i < tally.Length; i++) total += tally[i];
                _subtitle.text = L10n.T(L10nKeys.Stats_Subtitle,
                    r.Year, r.Month, total, (int)(r.Turnout * 100));
                ageData = r.VotesByAgeParty;
                eduData = r.VotesByEduParty;
                wealthData = r.VotesByWealthParty;
            }

            float y = 0f;

            // 1. Shared party legend
            y = DrawPartyLegend(y);

            // 2. Senate & Election summary
            if (dr == null)
            {
                y = DrawSenateSummary(y, r);
            }
            else
            {
                y = DrawDistrictSenateAndResults(y, dr);
            }

            // 3. Grievance chart
            y = DrawGrievanceChart(y, r, tally, total);

            // 4. Demographic stacked bars with enhanced hover tooltips
            y = DrawStackedChart(y, L10n.T(L10nKeys.Stats_Chart_ByAge),
                ageData,
                new[] {
                    L10n.T(L10nKeys.Bucket_Age_Young),
                    L10n.T(L10nKeys.Bucket_Age_Adult),
                    L10n.T(L10nKeys.Bucket_Age_Senior)
                });

            y = DrawStackedChart(y, L10n.T(L10nKeys.Stats_Chart_ByEducation),
                eduData,
                new[] {
                    L10n.T(L10nKeys.Bucket_Edu_Uneducated),
                    L10n.T(L10nKeys.Bucket_Edu_Educated),
                    L10n.T(L10nKeys.Bucket_Edu_WellEducated),
                    L10n.T(L10nKeys.Bucket_Edu_HighlyEducated)
                });

            y = DrawStackedChart(y, L10n.T(L10nKeys.Stats_Chart_ByWealth),
                wealthData,
                new[] {
                    L10n.T(L10nKeys.Bucket_Wealth_Low),
                    L10n.T(L10nKeys.Bucket_Wealth_Medium),
                    L10n.T(L10nKeys.Bucket_Wealth_High)
                });
        }

        private float DrawPartyLegend(float y)
        {
            var hdr = _chartPanel.AddUIComponent<UILabel>();
            hdr.text = L10n.T(L10nKeys.Stats_PartyColors);
            hdr.textScale = 0.85f;
            hdr.relativePosition = new Vector3(0, y);
            y += 20f;

            float x = 0f;
            foreach (var p in Config.Parties)
            {
                var swatch = _chartPanel.AddUIComponent<UIPanel>();
                swatch.backgroundSprite = "GenericPanel";
                swatch.color = p.Color;
                swatch.size = new Vector2(14, 14);
                swatch.relativePosition = new Vector3(x, y + 2);

                var lbl = _chartPanel.AddUIComponent<UILabel>();
                lbl.textScale = 0.75f;
                lbl.text = p.ShortName;
                lbl.relativePosition = new Vector3(x + 18, y + 2);
                x += 18 + Mathf.Max(40f, p.ShortName.Length * 9f);
            }

            return y + 28f;
        }

        private float DrawSenateSummary(float y, ElectionResult r)
        {
            var hdr = _chartPanel.AddUIComponent<UILabel>();
            hdr.text = L10n.T(L10nKeys.Stats_Senate_Title);
            hdr.textScale = 0.9f;
            hdr.relativePosition = new Vector3(0, y);
            y += 20f;

            var box = _chartPanel.AddUIComponent<UIPanel>();
            box.relativePosition = new Vector3(0, y);
            box.size = new Vector2(_chartPanel.width - 15f, 32f);
            box.backgroundSprite = "GenericPanel";
            box.color = new Color32(35, 38, 45, 200);

            var sb = new StringBuilder();
            sb.Append(string.Format(L10n.T(L10nKeys.Stats_Senate_Summary), r.TotalSenateSeats));
            if (r.SenateSeatsByParty != null && r.SenateSeatsByParty.Length > 0)
            {
                sb.Append("  •  ");
                bool first = true;
                for (int i = 0; i < r.SenateSeatsByParty.Length && i < Config.Parties.Length; i++)
                {
                    int seats = r.SenateSeatsByParty[i];
                    if (seats <= 0) continue;
                    if (!first) sb.Append(", ");
                    sb.Append(string.Format("{0} {1}", Config.Parties[i].ShortName, string.Format(L10n.T(L10nKeys.Stats_Senate_SeatSuffix), seats)));
                    first = false;
                }
                if (first) sb.Append(L10n.T(L10nKeys.Stats_Senate_NoWinner));
            }

            var lbl = box.AddUIComponent<UILabel>();
            lbl.text = sb.ToString();
            lbl.textScale = 0.8f;
            lbl.relativePosition = new Vector3(10, 8);
            lbl.textColor = new Color32(230, 230, 240, 255);

            return y + 42f;
        }

        private float DrawDistrictSenateAndResults(float y, DistrictResult dr)
        {
            // Banner for Senate winner in this district
            var banner = _chartPanel.AddUIComponent<UIPanel>();
            banner.relativePosition = new Vector3(0, y);
            banner.size = new Vector2(_chartPanel.width - 15f, 32f);
            banner.backgroundSprite = "GenericPanel";
            banner.color = new Color32(40, 50, 60, 220);

            var bannerLbl = banner.AddUIComponent<UILabel>();
            bannerLbl.textScale = 0.85f;
            bannerLbl.relativePosition = new Vector3(10, 8);

            if (dr.DistrictId == 0)
            {
                bannerLbl.text = L10n.T(L10nKeys.Stats_Senate_NoWinner);
                bannerLbl.textColor = new Color32(190, 190, 200, 255);
            }
            else if (dr.SenateWinnerParty >= 0 && dr.SenateWinnerParty < Config.Parties.Length)
            {
                var winParty = Config.Parties[dr.SenateWinnerParty];
                float winShare = (dr.VoteShareByParty != null && dr.SenateWinnerParty < dr.VoteShareByParty.Length)
                    ? dr.VoteShareByParty[dr.SenateWinnerParty] : 0f;
                int winVotes = (dr.VotesByParty != null && dr.SenateWinnerParty < dr.VotesByParty.Length)
                    ? dr.VotesByParty[dr.SenateWinnerParty] : 0;

                bannerLbl.text = string.Format(L10n.T(L10nKeys.Stats_Senate_Winner), winParty.FullName, winShare, winVotes);
                bannerLbl.textColor = Color.Lerp(winParty.Color, Color.white, 0.4f);
            }
            else
            {
                bannerLbl.text = L10n.T(L10nKeys.Stats_Senate_NoWinner);
                bannerLbl.textColor = new Color32(190, 190, 200, 255);
            }
            y += 40f;

            // District Party Breakdown Bars
            var hdr = _chartPanel.AddUIComponent<UILabel>();
            hdr.text = L10n.T(L10nKeys.Stats_District_PartyResults);
            hdr.textScale = 0.9f;
            hdr.relativePosition = new Vector3(0, y);
            y += 22f;

            float chartW = _chartPanel.width - 10f;
            float rowH = 22f;
            float barStart = 130f;
            float barW = chartW - 220f;

            if (dr.VotesByParty != null)
            {
                for (int p = 0; p < dr.VotesByParty.Length && p < Config.Parties.Length; p++)
                {
                    var party = Config.Parties[p];
                    int v = dr.VotesByParty[p];
                    float frac = dr.TotalVotes > 0 ? (float)v / dr.TotalVotes : 0f;

                    var pLbl = _chartPanel.AddUIComponent<UILabel>();
                    pLbl.text = party.ShortName;
                    pLbl.textScale = 0.75f;
                    pLbl.relativePosition = new Vector3(0, y + 2);
                    pLbl.size = new Vector2(120, rowH);

                    var bg = _chartPanel.AddUIComponent<UIPanel>();
                    bg.relativePosition = new Vector3(barStart, y + 2);
                    bg.size = new Vector2(barW, rowH - 6);
                    bg.backgroundSprite = "GenericPanel";
                    bg.color = new Color32(40, 40, 45, 180);

                    var fg = _chartPanel.AddUIComponent<UIPanel>();
                    fg.relativePosition = new Vector3(barStart, y + 2);
                    fg.size = new Vector2(Mathf.Max(2f, barW * frac), rowH - 6);
                    fg.backgroundSprite = "GenericPanel";
                    fg.color = party.Color;

                    var pctLbl = _chartPanel.AddUIComponent<UILabel>();
                    pctLbl.textScale = 0.75f;
                    pctLbl.text = L10n.T(L10nKeys.Stats_PctCountFormat, frac, v);
                    pctLbl.relativePosition = new Vector3(chartW - 85, y + 2);
                    pctLbl.size = new Vector2(85, rowH);

                    string tip = string.Format("{0}: {1} ({2:P1})\n{3}: {4}",
                        party.FullName,
                        L10n.T(L10nKeys.Stats_Votes_Suffix, v),
                        frac,
                        dr.DistrictName,
                        L10n.T(L10nKeys.Stats_Votes_Suffix, dr.TotalVotes));
                    pLbl.tooltip = tip;
                    bg.tooltip = tip;
                    fg.tooltip = tip;
                    pctLbl.tooltip = tip;

                    y += rowH;
                }
            }

            return y + 12f;
        }

        private float DrawGrievanceChart(float y, ElectionResult r, int[] tally, int total)
        {
            var hdr = _chartPanel.AddUIComponent<UILabel>();
            hdr.text = L10n.T(L10nKeys.Stats_WhyPeopleVoted);
            hdr.textScale = 0.9f;
            hdr.relativePosition = new Vector3(0, y);
            y += 22f;

            string[] labels = new[]
            {
                L10n.T(L10nKeys.Stats_Grievance_Ideology),
                L10n.T(L10nKeys.Stats_Grievance_HighTaxes),
                L10n.T(L10nKeys.Stats_Grievance_PoorHealth),
                L10n.T(L10nKeys.Stats_Grievance_HighCrime),
                L10n.T(L10nKeys.Stats_Grievance_PoorEducation),
                L10n.T(L10nKeys.Stats_Grievance_Unemployment),
                L10n.T(L10nKeys.Stats_Grievance_Pollution),
                L10n.T(L10nKeys.Stats_Grievance_LowLandValue),
                L10n.T(L10nKeys.Stats_Grievance_NoiseTrash)
            };
            var colors = new Color32[]
            {
                new Color32(180, 180, 190, 255),
                new Color32(255, 152, 0, 255),
                new Color32(244, 67, 54, 255),
                new Color32(103, 58, 183, 255),
                new Color32(63, 181, 235, 255),
                new Color32(233, 30, 99, 255),
                new Color32(76, 175, 80, 255),
                new Color32(255, 235, 59, 255),
                new Color32(156, 204, 101, 255),
            };
            int rows = Math.Min(labels.Length, tally.Length);
            float rowH = 22f;
            float chartW = _chartPanel.width - 10f;
            for (int i = 0; i < rows; i++)
            {
                int v = tally[i];
                float frac = total > 0 ? v / (float)total : 0f;

                var rowLabel = _chartPanel.AddUIComponent<UILabel>();
                rowLabel.text = labels[i];
                rowLabel.textScale = 0.75f;
                rowLabel.relativePosition = new Vector3(0, y + 2);
                rowLabel.size = new Vector2(120, rowH);

                var bg = _chartPanel.AddUIComponent<UIPanel>();
                bg.relativePosition = new Vector3(130, y + 2);
                bg.size = new Vector2(chartW - 220, rowH - 6);
                bg.backgroundSprite = "GenericPanel";
                bg.color = new Color32(40, 40, 45, 180);

                var fg = _chartPanel.AddUIComponent<UIPanel>();
                fg.relativePosition = new Vector3(130, y + 2);
                fg.size = new Vector2(Mathf.Max(2f, (chartW - 220) * frac), rowH - 6);
                fg.backgroundSprite = "GenericPanel";
                fg.color = colors[i];

                var pctLbl = _chartPanel.AddUIComponent<UILabel>();
                pctLbl.textScale = 0.75f;
                pctLbl.text = L10n.T(L10nKeys.Stats_PctCountFormat, frac, v);
                pctLbl.relativePosition = new Vector3(chartW - 85, y + 2);
                pctLbl.size = new Vector2(85, rowH);

                string tip = string.Format("{0}: {1} ({2:P1})", labels[i], L10n.T(L10nKeys.Stats_Votes_Suffix, v), frac);
                rowLabel.tooltip = tip;
                bg.tooltip = tip;
                fg.tooltip = tip;
                pctLbl.tooltip = tip;

                y += rowH;
            }

            return y + 10f;
        }

        /// <summary>
        /// Draw one stacked bar per demographic bucket showing the party split.
        /// Enhanced with complete per-party breakdown hover tooltips.
        /// </summary>
        private float DrawStackedChart(float y, string title, int[,] data, string[] bucketLabels)
        {
            var hdr = _chartPanel.AddUIComponent<UILabel>();
            hdr.text = title;
            hdr.textScale = 0.9f;
            hdr.relativePosition = new Vector3(0, y);
            y += 22f;

            if (data == null)
            {
                var msg = _chartPanel.AddUIComponent<UILabel>();
                msg.text = L10n.T(L10nKeys.Stats_Chart_NoData);
                msg.textScale = 0.75f;
                msg.textColor = new Color32(170, 170, 170, 255);
                msg.relativePosition = new Vector3(10, y);
                return y + 22f;
            }

            int buckets = data.GetLength(0);
            int parties = data.GetLength(1);
            float chartW = _chartPanel.width - 10f;
            float rowH = 22f;
            float barStart = 130f;
            float barW = chartW - 220f;

            for (int b = 0; b < buckets && b < bucketLabels.Length; b++)
            {
                int total = 0;
                for (int p = 0; p < parties; p++) total += data[b, p];

                // Build sorted per-party votes for the category tooltip
                var partyVotes = new List<KeyValuePair<int, int>>();
                for (int p = 0; p < parties; p++)
                {
                    if (p < Config.Parties.Length)
                        partyVotes.Add(new KeyValuePair<int, int>(p, data[b, p]));
                }
                partyVotes.Sort((k1, k2) => k2.Value.CompareTo(k1.Value)); // descending order

                var sb = new StringBuilder();
                sb.AppendLine(string.Format("[{0}] {1} (100.0%)", bucketLabels[b], L10n.T(L10nKeys.Stats_Votes_Suffix, total)));
                sb.AppendLine("────────────────────────");
                if (total > 0)
                {
                    foreach (var kvp in partyVotes)
                    {
                        int p = kvp.Key;
                        int votes = kvp.Value;
                        float pct = (float)votes / total;
                        sb.AppendLine(string.Format("■ {0}: {1} ({2:P1})", Config.Parties[p].FullName, votes, pct));
                    }
                }
                else
                {
                    sb.AppendLine("(0 votes)");
                }
                string categoryTooltip = sb.ToString().TrimEnd();

                var lbl = _chartPanel.AddUIComponent<UILabel>();
                lbl.text = bucketLabels[b];
                lbl.textScale = 0.75f;
                lbl.relativePosition = new Vector3(0, y + 2);
                lbl.size = new Vector2(120, rowH);
                lbl.tooltip = categoryTooltip;

                var bg = _chartPanel.AddUIComponent<UIPanel>();
                bg.relativePosition = new Vector3(barStart, y + 2);
                bg.size = new Vector2(barW, rowH - 6);
                bg.backgroundSprite = "GenericPanel";
                bg.color = new Color32(40, 40, 45, 180);
                bg.tooltip = categoryTooltip;

                float xCursor = 0f;
                for (int p = 0; p < parties; p++)
                {
                    if (p >= Config.Parties.Length) break;
                    if (total <= 0) break;
                    int votes = data[b, p];
                    float frac = votes / (float)total;
                    if (frac <= 0f) continue;
                    var seg = _chartPanel.AddUIComponent<UIPanel>();
                    seg.relativePosition = new Vector3(barStart + xCursor, y + 2);
                    seg.size = new Vector2(Mathf.Max(1f, barW * frac), rowH - 6);
                    seg.backgroundSprite = "GenericPanel";
                    seg.color = Config.Parties[p].Color;

                    // Hover tooltip on the party segment
                    seg.tooltip = string.Format(
                        "[{0}]\n{1}: {2} ({3:P1})\n({4}: {5})",
                        Config.Parties[p].FullName,
                        bucketLabels[b],
                        votes,
                        frac,
                        L10n.T(L10nKeys.Stats_CategoryTotal_Label),
                        total
                    );

                    xCursor += barW * frac;
                }

                var totalLbl = _chartPanel.AddUIComponent<UILabel>();
                totalLbl.textScale = 0.75f;
                totalLbl.text = L10n.T(L10nKeys.Stats_Votes_Suffix, total);
                totalLbl.relativePosition = new Vector3(chartW - 85, y + 2);
                totalLbl.size = new Vector2(85, rowH);
                totalLbl.tooltip = categoryTooltip;

                y += rowH;
            }

            return y + 10f;
        }
    }
}
