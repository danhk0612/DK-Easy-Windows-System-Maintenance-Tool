using System.Windows;
using System.Windows.Controls;

namespace DK.Easy.WindowsMaintenance;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UpdateSelectedPage();
    }

    private void NavigationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedPage();
    }

    private void UpdateSelectedPage()
    {
        // 초기 XAML 로딩 중 SelectionChanged가 먼저 호출될 수 있습니다.
        if (PageTitle is null || PageDescription is null)
        {
            return;
        }

        var title = (NavigationList.SelectedItem as ListBoxItem)?.Content?.ToString() ?? "대시보드";
        PageTitle.Text = title;

        PageDescription.Text = title switch
        {
            "대시보드" => "Windows와 시스템 드라이브의 상태, 마지막 검사 결과를 확인하는 화면입니다.",
            "저장 공간" => "대용량 파일·폴더와 임시파일을 조회하고 선택적으로 정리할 화면입니다.",
            "시작 프로그램" => "로그인 시 자동 실행되는 항목을 확인하고 관리할 화면입니다.",
            "서비스" => "타사 서비스의 상태와 시작 유형을 확인할 화면입니다.",
            "예약 작업" => "작업 스케줄러의 등록 작업을 확인할 화면입니다.",
            "설치 프로그램" => "설치된 앱의 정보와 기존 제거 기능을 사용할 화면입니다.",
            "점검 기록" => "이전 검사와 현재 결과를 비교할 화면입니다.",
            _ => "아직 구현되지 않은 화면입니다."
        };
    }
}
