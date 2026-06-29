using System.Globalization;
using System.Text;

namespace TokenUsage.Providers.DeepSeek;

internal static class DeepSeekRenderer
{
    public static string Render(BalanceResponse response)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DeepSeek 余额");
        sb.AppendLine("=============");
        sb.AppendLine();
        sb.Append("可用: ");
        sb.AppendLine(response.IsAvailable ? "是" : "否");

        if (response.BalanceInfos.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("账户无可用余额");
        }
        else
        {
            foreach (var info in response.BalanceInfos)
            {
                sb.AppendLine();
                sb.Append("[").Append(info.Currency).AppendLine("]");
                sb.Append("  总余额:    ").Append(FormatAmount(info.Currency, info.TotalBalance)).AppendLine();
                sb.Append("  充值余额:  ").Append(FormatAmount(info.Currency, info.ToppedUpBalance)).AppendLine();
                sb.Append("  未过期赠金: ").Append(FormatAmount(info.Currency, info.GrantedBalance)).AppendLine();
            }
        }

        sb.AppendLine();
        sb.Append("刷新时间: ");
        sb.Append(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));

        return sb.ToString();
    }

    private static string FormatAmount(string currency, string amount)
    {
        var symbol = currency switch
        {
            "CNY" => "¥",
            "USD" => "$",
            _ => string.Empty,
        };
        return $"{symbol}{amount}";
    }
}