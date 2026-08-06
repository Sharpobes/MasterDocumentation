using MasterDocumentation.Utilities;

namespace MasterDocumentation.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void EnglishCatalog_TranslatesInterfaceAndCanSwitchBackToRussian()
    {
        try
        {
            LocalizationService.SetLanguage(LocalizationService.English);

            Assert.Equal("Interface language",LocalizationService.T("Язык интерфейса"));
            Assert.Equal("Theme",LocalizationService.T("Тема"));
            Assert.Equal("Do you want to open this link?\n\nhttps://example.com/",
                LocalizationService.T("Хотите перейти по ссылке?\n\nhttps://example.com/"));

            LocalizationService.SetLanguage(LocalizationService.Russian);
            Assert.Equal("Язык интерфейса",LocalizationService.T("Язык интерфейса"));
        }
        finally
        {
            LocalizationService.SetLanguage(LocalizationService.Russian);
        }
    }

    [Fact]
    public void Apply_TranslatesStaticAndLaterUpdatedWpfTextWithoutChangingSettingValue()
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            try
            {
                LocalizationService.SetLanguage(LocalizationService.English);
                var panel=new System.Windows.Controls.StackPanel();
                var label=new System.Windows.Controls.TextBlock{Text="Язык интерфейса"};
                var language=new System.Windows.Controls.ComboBoxItem{Content="Русский"};
                panel.Children.Add(label);
                panel.Children.Add(language);

                LocalizationService.Apply(panel);

                Assert.Equal("Interface language",label.Text);
                Assert.Equal("Russian",language.Content);
                Assert.Equal("Русский",LocalizationService.SourceContent(language));

                label.Text="Настройки";
                Assert.Equal("Settings",label.Text);

                LocalizationService.SetLanguage(LocalizationService.Russian);
                LocalizationService.Apply(panel);
                Assert.Equal("Настройки",label.Text);
                Assert.Equal("Русский",language.Content);
            }
            catch(Exception ex){failure=ex;}
            finally{LocalizationService.SetLanguage(LocalizationService.Russian);}
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if(failure is not null)throw failure;
    }
}
