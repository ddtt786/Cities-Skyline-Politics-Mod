using System.Collections.Generic;

namespace PoliticsMod.Localization.Languages
{
    // Master Korean catalog.
    public static class Ko
    {
        public static Language Build()
        {
            var s = new Dictionary<string, string>();

            // Mod metadata
            s[L10nKeys.Mod_Name] = "정치 & 선거 모드 (Politics & Elections)";
            s[L10nKeys.Mod_Description] = "시민들이 의회를 선출하고 연립정부가 도시 정책을 결정합니다. Ctrl+P를 눌러 패널을 여세요.";

            // Settings
            s[L10nKeys.Settings_Group_Main] = "정치 & 선거 설정";
            s[L10nKeys.Settings_EnableDebugLogging] = "디버그 로그 활성화";
            s[L10nKeys.Settings_Group_Hotkey] = "패널 단축키";
            s[L10nKeys.Settings_Hotkey] = "단축키";
            s[L10nKeys.Settings_RequireCtrl] = "Ctrl 조합키 필수";
            s[L10nKeys.Settings_Group_Utilities] = "도구";
            s[L10nKeys.Settings_OpenElectionsPanel] = "선거 패널 열기";
            s[L10nKeys.Settings_Language] = "언어";
            s[L10nKeys.Settings_Language_Auto] = "자동 (게임 언어 따름)";

            // Common
            s[L10nKeys.Common_CloseX] = "X";

            // Main panel
            s[L10nKeys.Panel_Title] = "정치 & 선거";
            s[L10nKeys.Panel_Hemi_Tooltip] = "의회 정원은 인구에 비례하여 조정됩니다:\n시민 {0}명당 1석 (최소 {1}석, 최대 {2}석).";
            s[L10nKeys.Panel_ActivePolicies] = "시행 중인 정책:";
            s[L10nKeys.Panel_ActivePolicies_None] = "(없음)";
            s[L10nKeys.Panel_Overlay_Prefix] = "오버레이: {0}";
            s[L10nKeys.Panel_Button_CallSnapElection] = "조기 선거 실시";
            s[L10nKeys.Panel_Button_ManageParties] = "정당 관리";
            s[L10nKeys.Panel_Button_VoterTraits] = "유권자 성향";
            s[L10nKeys.Panel_Button_ElectionStats] = "선거 통계";
            s[L10nKeys.Panel_Button_OpinionPolling] = "여론 조사";
            s[L10nKeys.Panel_MinimizeChirps] = "트윗 알림 최소화";
            s[L10nKeys.Panel_MinimizeChirps_Tooltip] = "필수 알림만 표시합니다: 선거 운동 시작, 선거 결과, 법안 통과/폐기.";
            s[L10nKeys.Panel_ElectionTimings] = "선거 일정 (설정 가능)";
            s[L10nKeys.Panel_Slider_TermLength] = "임기 기간";
            s[L10nKeys.Panel_Slider_CampaignLength] = "선거 운동 기간";
            s[L10nKeys.Panel_Slider_ReElectionCooldown] = "재선거 대기 기간";
            s[L10nKeys.Panel_Slider_ReElectionCooldown_Tooltip] =
                "연정 협상이 결렬되었을 때 조기 재선거가 열리기까지 대기하는 인게임 일수입니다.";
            s[L10nKeys.Panel_Days] = "{0}일";
            s[L10nKeys.Panel_Phase_Campaign] = "상태: {0} | 선거 운동 {1}/{2}일차";
            s[L10nKeys.Panel_Phase_Term] = "상태: {0} | 임기 {1}/{2}일차";
            s[L10nKeys.Phase_Idle] = "대기";
            s[L10nKeys.Phase_Campaign] = "선거 운동";
            s[L10nKeys.Phase_Voting] = "투표 중";
            s[L10nKeys.Phase_Forming] = "연정 협상";
            s[L10nKeys.Phase_Governing] = "집권 중";
            s[L10nKeys.Phase_Failed] = "협상 결렬";
            s[L10nKeys.Panel_Coalition_Header] = "연립 정부: {0}  ({1}/{2}석)";
            s[L10nKeys.Panel_Coalition_None] = "연립 정부: (없음)";
            s[L10nKeys.Panel_Policies_More] = "+{0}";

            // Overlay
            s[L10nKeys.Overlay_Off] = "끔";
            s[L10nKeys.Overlay_Party] = "정당 지지";
            s[L10nKeys.Overlay_Turnout] = "투표율";
            s[L10nKeys.Overlay_Satisfaction] = "만족도";
            s[L10nKeys.InfoButton_Prefix] = "정치: {0}";

            // Party editor
            s[L10nKeys.PartyEditor_Title] = "정당 관리";
            s[L10nKeys.PartyEditor_Add] = "+ 정당 추가";
            s[L10nKeys.PartyEditor_Remove] = "– 삭제";
            s[L10nKeys.PartyEditor_ShortName] = "약칭";
            s[L10nKeys.PartyEditor_FullName] = "정식 당명";
            s[L10nKeys.PartyEditor_Color] = "상징색";
            s[L10nKeys.PartyEditor_IdeologyHeader] = "이념 성향 (-1 ... +1)";
            s[L10nKeys.PartyEditor_Ideology_Economic] = "경제 (좌파↔우파)";
            s[L10nKeys.PartyEditor_Ideology_Social] = "사회 (진보↔보수)";
            s[L10nKeys.PartyEditor_Ideology_Governance] = "정치 (자유↔권위)";
            s[L10nKeys.PartyEditor_PoliciesHeader] = "공약 정책 (클릭하여 전환: 중립 -> 찬성 -> 반대)";
            s[L10nKeys.PartyEditor_Stance_Support] = "{0}\n찬성: 집권 시 도입됩니다";
            s[L10nKeys.PartyEditor_Stance_Oppose] = "{0}\n반대: 집권 시 폐지됩니다";
            s[L10nKeys.PartyEditor_Stance_Neutral] = "{0}\n중립: 현행 유지";
            s[L10nKeys.PartyEditor_TaxHeader] = "세율 공약 (%p, -10 .. +10)";
            s[L10nKeys.PartyEditor_Tax_Res] = "주거 지역";
            s[L10nKeys.PartyEditor_Tax_Com] = "상업 지역";
            s[L10nKeys.PartyEditor_Tax_Ind] = "산업 지역";
            s[L10nKeys.PartyEditor_Tax_Off] = "오피스 지역";
            s[L10nKeys.PartyEditor_BudgetHeader] = "예산 공약 (%p, -30 .. +30)";
            s[L10nKeys.PartyEditor_Budget_Electricity] = "전기";
            s[L10nKeys.PartyEditor_Budget_Water] = "수도";
            s[L10nKeys.PartyEditor_Budget_Garbage] = "쓰레기";
            s[L10nKeys.PartyEditor_Budget_Healthcare] = "의료";
            s[L10nKeys.PartyEditor_Budget_Fire] = "소방";
            s[L10nKeys.PartyEditor_Budget_Police] = "경찰";
            s[L10nKeys.PartyEditor_Budget_Education] = "교육";
            s[L10nKeys.PartyEditor_Budget_Transport] = "대중교통";
            s[L10nKeys.PartyEditor_Budget_Beautification] = "공원 및 미화";
            s[L10nKeys.PartyEditor_Budget_Roads] = "도로";
            s[L10nKeys.PartyEditor_Budget_Industry] = "산업";
            s[L10nKeys.PartyEditor_NewPartyShortName] = "신당{0}";
            s[L10nKeys.PartyEditor_NewPartyFullName] = "새로운 정당 {0}";

            // Voter Traits
            s[L10nKeys.VoterTraits_Title] = "유권자 성향 - 경제 축 편향 (-1 좌파 ... +1 우파)";
            s[L10nKeys.VoterTraits_Note] =
                "계층별 경제 성향을 미세 조정합니다. 음수 = 좌파(복지 확대, 증세), 양수 = 우파(감세, 친기업).\n청년, 성인, 노인 시민만 투표권을 갖습니다.";
            s[L10nKeys.VoterTraits_Section_Education] = "교육 수준";
            s[L10nKeys.VoterTraits_Edu_Uneducated] = "무학 (초등 미만)";
            s[L10nKeys.VoterTraits_Edu_Educated] = "초등 교육";
            s[L10nKeys.VoterTraits_Edu_WellEducated] = "중등 교육 (고졸)";
            s[L10nKeys.VoterTraits_Edu_HighlyEducated] = "고등 교육 (대졸 이상)";
            s[L10nKeys.VoterTraits_Section_Wealth] = "소득 수준";
            s[L10nKeys.VoterTraits_Wealth_Low] = "저소득층";
            s[L10nKeys.VoterTraits_Wealth_Medium] = "중산층";
            s[L10nKeys.VoterTraits_Wealth_High] = "고소득층";
            s[L10nKeys.VoterTraits_Section_Employment] = "고용 상태";
            s[L10nKeys.VoterTraits_Employment_Employed] = "취업자";
            s[L10nKeys.VoterTraits_Employment_Unemployed] = "실업자";
            s[L10nKeys.VoterTraits_Section_Age] = "연령대";
            s[L10nKeys.VoterTraits_Age_Young] = "청년";
            s[L10nKeys.VoterTraits_Age_Adult] = "성인";
            s[L10nKeys.VoterTraits_Age_Senior] = "노인";
            s[L10nKeys.VoterTraits_Section_Life] = "생활 환경";
            s[L10nKeys.VoterTraits_Life_Sick] = "환자 (질병 상태)";
            s[L10nKeys.VoterTraits_Life_Pollution] = "공해 지역 거주자";
            s[L10nKeys.VoterTraits_Section_Deficit] = "재정 적자 반응";
            s[L10nKeys.VoterTraits_Deficit_Label] = "적자 압박 가중치";
            s[L10nKeys.VoterTraits_Deficit_Tooltip] = "지속적인 재정 적자 시 유권자가 우파로 이동하는 민감도입니다.";
            s[L10nKeys.VoterTraits_Section_Incumbency] = "여당 프리미엄";
            s[L10nKeys.VoterTraits_Incumbency_Label] = "현직 프리미엄";
            s[L10nKeys.VoterTraits_Incumbency_Tooltip] = "도시 만족도가 60 이상인 행복한 유권자가 현 여당을 재지지할 확률입니다.";
            s[L10nKeys.VoterTraits_Reset] = "기본값으로 초기화";

            // Election Stats
            s[L10nKeys.Stats_Title] = "선거 상세 통계";
            s[L10nKeys.Stats_NoData_Subtitle] = "아직 선거 데이터가 없습니다.";
            s[L10nKeys.Stats_NoData_Body] = "기록된 선거 결과가 없습니다. 선거가 치러지면 상세 통계가 표시됩니다.";
            s[L10nKeys.Stats_Subtitle] = "{0}년 {1}월 선거 - 총 {2}표 투표  •  투표율 {3}%";
            s[L10nKeys.Stats_PartyColors] = "정당 상징색";
            s[L10nKeys.Stats_WhyPeopleVoted] = "투표 이유 (주요 동기)";
            s[L10nKeys.Stats_Grievance_Ideology] = "순수 이념 지지";
            s[L10nKeys.Stats_Grievance_HighTaxes] = "높은 세금 불만";
            s[L10nKeys.Stats_Grievance_PoorHealth] = "열악한 의료 환경";
            s[L10nKeys.Stats_Grievance_HighCrime] = "높은 범죄율 불만";
            s[L10nKeys.Stats_Grievance_PoorEducation] = "교육 시설 부족";
            s[L10nKeys.Stats_Grievance_Unemployment] = "실업 문제";
            s[L10nKeys.Stats_Grievance_Pollution] = "환경 공해 불만";
            s[L10nKeys.Stats_Grievance_LowLandValue] = "낮은 지가 불만";
            s[L10nKeys.Stats_Grievance_NoiseTrash] = "소음 및 쓰레기 문제";
            s[L10nKeys.Stats_Chart_ByAge] = "연령대별 투표 분포";
            s[L10nKeys.Stats_Chart_ByEducation] = "교육 수준별 투표 분포";
            s[L10nKeys.Stats_Chart_ByWealth] = "소득 수준별 투표 분포";
            s[L10nKeys.Stats_Chart_NoData] = "(데이터 없음 - 이전 버전 기록)";
            s[L10nKeys.Stats_Votes_Suffix] = "{0}표";
            s[L10nKeys.Stats_PctCountFormat] = "{0:P1}  ({1}표)";

            // New: Election History, Districts, Senate & Tooltips
            s[L10nKeys.Stats_Election_Label] = "선거 회차:";
            s[L10nKeys.Stats_Election_Item] = "제{0}회 선거 ({1}년 {2}월)";
            s[L10nKeys.Stats_District_Label] = "영역(구역):";
            s[L10nKeys.Stats_District_CityWide] = "전체 도시 (City-Wide)";
            s[L10nKeys.Stats_District_Unzoned] = "미지정 외곽 지역";
            s[L10nKeys.Stats_District_PartyResults] = "구역 내 정당별 득표 결과";
            s[L10nKeys.Stats_Senate_Title] = "상원 (구역별 1석 소선거구제)";
            s[L10nKeys.Stats_Senate_Summary] = "상원 의석: 총 {0}석";
            s[L10nKeys.Stats_Senate_Winner] = "★ 상원의원 당선: {0} ({1:P1}, {2}표)";
            s[L10nKeys.Stats_Senate_NoWinner] = "상원의원 없음 (비행정 구역)";
            s[L10nKeys.Stats_Senate_SeatSuffix] = "{0}석";
            s[L10nKeys.Stats_CategoryTotal_Label] = "해당 카테고리 총계";
            s[L10nKeys.Panel_Senate_Summary] = "상원: {0}";

            s[L10nKeys.Bucket_Age_Young] = "청년";
            s[L10nKeys.Bucket_Age_Adult] = "성인";
            s[L10nKeys.Bucket_Age_Senior] = "노인";
            s[L10nKeys.Bucket_Edu_Uneducated] = "무학 (초등 미만)";
            s[L10nKeys.Bucket_Edu_Educated] = "초등 교육";
            s[L10nKeys.Bucket_Edu_WellEducated] = "중등 교육 (고졸)";
            s[L10nKeys.Bucket_Edu_HighlyEducated] = "고등 교육 (대졸 이상)";
            s[L10nKeys.Bucket_Wealth_Low] = "저소득층";
            s[L10nKeys.Bucket_Wealth_Medium] = "중산층";
            s[L10nKeys.Bucket_Wealth_High] = "고소득층";

            // Opinion Polling
            s[L10nKeys.Polling_Title] = "일일 여론 조사";
            s[L10nKeys.Polling_NoHistory] = "아직 여론조사 데이터가 없습니다. 매일 샘플이 수집됩니다.";
            s[L10nKeys.Polling_Subtitle] = "일일 여론조사 - 표본 크기 {0}명 - 최근 {1}일간 추이";

            return new Language("ko", "한국어", s);
        }
    }
}

