using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Atomex;
using Atomex.Abstract;
using Atomex.Blockchain.Abstract;
using Atomex.Common;
using Atomex.Core;
using Atomex.MarketData;
using Atomex.MarketData.Abstract;
using Atomex.Subsystems;
using Atomex.Subsystems.Abstract;
using Atomex.Swaps;
using Serilog;
using Microsoft.JSInterop;

namespace atomex_frontend.Storages
{
  public class SwapStorage
  {
    public SwapStorage(
      AccountStorage accountStorage,
      WalletStorage walletStorage,
      Toolbelt.Blazor.I18nText.I18nText I18nText,
      IJSRuntime JSRuntime)
    {
      this.jSRuntime = JSRuntime;
      this.accountStorage = accountStorage;
      this.walletStorage = walletStorage;

      this.accountStorage.InitializeCallback += SubscribeToServices;
      this.walletStorage.RefreshMarket += (bool force) => UpdateAmount(0, force);
      LoadTranslations(I18nText);
    }

    private IJSRuntime jSRuntime;
    I18nText.Translations Translations = new I18nText.Translations();
    private async void LoadTranslations(Toolbelt.Blazor.I18nText.I18nText I18nText)
    {
      Translations = await I18nText.GetTextTableAsync<I18nText.Translations>(null);
    }

    public event Action RefreshRequested;
    public void CallUIRefresh()
    {
      RefreshRequested?.Invoke();
    }
    private AccountStorage accountStorage;
    private WalletStorage walletStorage;

    private IAtomexApp App
    {
      get => accountStorage.AtomexApp;
    }

    public Currency FromCurrency
    {
      get => walletStorage.SelectedCurrency;
    }

    public Currency ToCurrency
    {
      get => walletStorage.SelectedSecondCurrency;
    }

    private ISymbols Symbols
    {
      get => App.Account.Symbols;
    }

    private ICurrencies Currencies
    {
      get => App.Account.Currencies;
    }

    private decimal _estimatedOrderPrice;
    private decimal EstimatedOrderPrice { get => _estimatedOrderPrice; }

    private decimal _estimatedPrice;
    public decimal EstimatedPrice
    {
      get => _estimatedPrice;
      set { _estimatedPrice = value; }
    }

    private decimal _estimatedPaymentFee;
    public decimal EstimatedPaymentFee
    {
      get => _estimatedPaymentFee;
      set { _estimatedPaymentFee = value; }
    }

    private decimal _amount;
    public decimal Amount
    {
      get => _amount;
      set { UpdateAmount(value); }
    }

    private decimal _targetAmount;
    public decimal TargetAmount
    {
      get => _targetAmount;
      set { _targetAmount = value; }
    }

    private bool _isAmountUpdating;
    public bool IsAmountUpdating
    {
      get => _isAmountUpdating;
      set { _isAmountUpdating = value; }
    }

    public decimal AmountDollars
    {
      get => walletStorage.GetDollarValue(walletStorage.SelectedCurrency.Name, this._amount);
    }

    public decimal TargetAmountDollars
    {
      get => walletStorage.GetDollarValue(walletStorage.SelectedSecondCurrency.Name, this._targetAmount);
    }

    private decimal _estimatedMaxAmount;
    public decimal EstimatedMaxAmount
    {
      get => _estimatedMaxAmount;
      set { _estimatedMaxAmount = value; }
    }

    private decimal _estimatedRedeemFee;
    public decimal EstimatedRedeemFee
    {
      get => _estimatedRedeemFee;
      set { _estimatedRedeemFee = value; }
    }

    private decimal _rewardForRedeem;
    public decimal RewardForRedeem
    {
      get => _rewardForRedeem;
      set
      {
        _rewardForRedeem = value;
        HasRewardForRedeem = _rewardForRedeem != 0;
      }
    }

    private bool _hasRewardForRedeem;
    public bool HasRewardForRedeem
    {
      get => _hasRewardForRedeem;
      set { _hasRewardForRedeem = value; }
    }

    private bool _isNoLiquidity;
    public bool IsNoLiquidity
    {
      get => _isNoLiquidity;
      set
      { _isNoLiquidity = value; }
    }

    protected string _warning;
    public string Warning
    {
      get => _warning;
      set { _warning = value; }
    }

    private long lastIncompletedSwapId;

    private static TimeSpan SwapTimeout = TimeSpan.FromSeconds(60);
    private static TimeSpan SwapCheckInterval = TimeSpan.FromSeconds(3);

    public IEnumerable<Swap> Swaps { get; set; } = new List<Swap>();

    private void SubscribeToServices(bool IsRestarting)
    {
      if (!IsRestarting)
      {
        App.TerminalChanged += OnTerminalChangedEventHandler;
        App.Terminal.QuotesUpdated += OnQuotesUpdatedEventHandler;
        App.Terminal.SwapUpdated += OnSwapEventHandler;
        App.Terminal.ServiceDisconnected += OnTerminalServiceStateChangedEventHandler;
        App.Terminal.ServiceConnected += OnTerminalServiceStateChangedEventHandler;
        Console.WriteLine("Subscribed to swap events in start");
      }
      OnSwapEventHandler(this, null);
    }

    private void OnTerminalServiceStateChangedEventHandler(object sender, TerminalServiceEventArgs args)
    {
      if (!(sender is IAtomexClient terminal))
        return;

      if (args.Service == TerminalService.MarketData)
      {
        if (Amount != 0)
        {
          Console.WriteLine($"UPDATING AMOUNT TO 0");
          UpdateAmount(0, true);
        }
      }
    }

    private void OnTerminalChangedEventHandler(object sender, TerminalChangedEventArgs args)
    {
      Console.WriteLine("SWAP: TERMINAL CHANGED EVENT");
      var terminal = args.Terminal;

      if (terminal?.Account == null)
        return;

      terminal.QuotesUpdated += OnQuotesUpdatedEventHandler;
      terminal.SwapUpdated += OnSwapEventHandler;
      Console.WriteLine("Subscribed to swap events");
      OnSwapEventHandler(this, null);
    }

    public async void OnQuotesUpdatedEventHandler(object sender, MarketDataEventArgs args)
    {
      try
      {
        if (!(sender is IAtomexClient terminal))
          return;

        if (ToCurrency == null)
          return;

        var symbol = Symbols.SymbolByCurrencies(FromCurrency, ToCurrency);
        if (symbol == null)
          return;

        var side = symbol.OrderSideForBuyCurrency(ToCurrency);
        var orderBook = terminal.GetOrderBook(symbol);

        if (orderBook == null)
          return;

        var walletAddress = await App.Account
            .GetRedeemAddressAsync(ToCurrency.FeeCurrencyName);

        var baseCurrency = Currencies.GetByName(symbol.Base);

        (_estimatedOrderPrice, _estimatedPrice) = orderBook.EstimateOrderPrices(
            side,
            Amount,
            FromCurrency.DigitsMultiplier,
            baseCurrency.DigitsMultiplier);

        _estimatedMaxAmount = orderBook.EstimateMaxAmount(side, FromCurrency.DigitsMultiplier);

        EstimatedRedeemFee = await ToCurrency.GetRedeemFeeAsync(walletAddress);

        _isNoLiquidity = Amount != 0 && _estimatedOrderPrice == 0;

        if (symbol.IsBaseCurrency(ToCurrency.Name))
        {
          _targetAmount = _estimatedPrice != 0
              ? AmountHelper.RoundDown(Amount / _estimatedPrice, ToCurrency.DigitsMultiplier)
              : 0m;
        }
        else if (symbol.IsQuoteCurrency(ToCurrency.Name))
        {
          _targetAmount = AmountHelper.RoundDown(Amount * _estimatedPrice, ToCurrency.DigitsMultiplier);
        }
        this.CallUIRefresh();
      }
      catch (Exception e)
      {
        Log.Error(e, "Quotes updated event handler error");
      }
    }

    private async void OnSwapEventHandler(object sender, SwapEventArgs args)
    {
      try
      {
        Swaps = (await App.Account
            .GetSwapsAsync())
            .ToList()
            .OrderByDescending(sw => sw.TimeStamp.ToUniversalTime());

        Console.WriteLine($"Finded {Swaps.Count()} swaps");

        if (args != null)
        {
          if (!args.Swap.IsComplete)
          {
            lastIncompletedSwapId = args.Swap.Id;
          }
          else
          {
            if (args.Swap.Id == lastIncompletedSwapId)
            {
              var description = $"Converting {AmountHelper.QtyToAmount(args.Swap.Side, args.Swap.Qty, args.Swap.Price, getCurrency(args.Swap.SoldCurrency).DigitsMultiplier)} {args.Swap.SoldCurrency} to {AmountHelper.QtyToAmount(args.Swap.Side.Opposite(), args.Swap.Qty, args.Swap.Price, getCurrency(args.Swap.PurchasedCurrency).DigitsMultiplier)} {args.Swap.PurchasedCurrency} successfully completed";
              Console.WriteLine(description);
              jSRuntime.InvokeVoidAsync("showNotification", $"Swap completed", description, null);
            }
          }
        }
        this.CallUIRefresh();
      }
      catch (Exception e)
      {
        Log.Error($"Swaps update error {e.ToString()}");
      }
    }

    private Currency getCurrency(string Currency)
    {
      return AccountStorage.Currencies.GetByName(Currency);
    }

    private async void UpdateAmount(decimal value, bool force = false)
    {
      Warning = string.Empty;
      try
      {
        if (value == _amount && !force)
        {
          return;
        }
        IsAmountUpdating = true;

        var previousAmount = _amount;
        _amount = value;
        this.CallUIRefresh();

        var (maxAmount, maxFee, reserve) = await App.Account
            .EstimateMaxAmountToSendAsync(FromCurrency.Name, null, BlockchainTransactionType.SwapPayment, 0, 0, true);

        var swaps = await App.Account
            .GetSwapsAsync();

        var usedAmount = swaps.Sum(s => (s.IsActive && s.SoldCurrency == FromCurrency.Name && !s.StateFlags.HasFlag(SwapStateFlags.IsPaymentConfirmed))
            ? s.Symbol.IsBaseCurrency(FromCurrency.Name)
                ? s.Qty
                : s.Qty * s.Price
            : 0);

        usedAmount = AmountHelper.RoundDown(usedAmount, FromCurrency.DigitsMultiplier);

        maxAmount = Math.Max(maxAmount - usedAmount, 0);

        var includeFeeToAmount = FromCurrency.FeeCurrencyName == FromCurrency.Name;

        var availableAmount = FromCurrency is BitcoinBasedCurrency
            ? walletStorage.SelectedCurrencyData.Balance
            : maxAmount + (includeFeeToAmount ? maxFee : 0);

        var estimatedPaymentFee = _amount != 0
            ? (_amount < availableAmount
                ? await App.Account
                    .EstimateFeeAsync(FromCurrency.Name, null, _amount, BlockchainTransactionType.SwapPayment)
                : null)
            : 0;

        if (estimatedPaymentFee == null)
        {
          if (maxAmount > 0)
          {
            _amount = maxAmount;
            estimatedPaymentFee = maxFee;
          }
          else
          {
            _amount = 0; // previousAmount;
            IsAmountUpdating = false;

            if (FromCurrency.Name != FromCurrency.FeeCurrencyName && walletStorage.SelectedCurrencyData.Balance > 0)
              Warning = string.Format(CultureInfo.InvariantCulture, Translations.CvInsufficientChainFunds, FromCurrency.FeeCurrencyName);

            this.CallUIRefresh();
            return;
            // todo: insufficient funds warning
            // 
          }
        }

        EstimatedPaymentFee = estimatedPaymentFee.Value;

        if (_amount + (includeFeeToAmount ? _estimatedPaymentFee : 0) > availableAmount)
          _amount = Math.Max(availableAmount - (includeFeeToAmount ? _estimatedPaymentFee : 0), 0);


        UpdateRedeemAndRewardFeesAsync();
        OnQuotesUpdatedEventHandler(App.Terminal, null);
      }
      finally
      {
        IsAmountUpdating = false;
      }
    }

    private async void UpdateRedeemAndRewardFeesAsync()
    {
      var walletAddress = await App.Account
          .GetRedeemAddressAsync(ToCurrency.FeeCurrencyName);

      EstimatedRedeemFee = await ToCurrency.GetRedeemFeeAsync(walletAddress);

      RewardForRedeem = walletAddress.AvailableBalance() < EstimatedRedeemFee && !(ToCurrency is BitcoinBasedCurrency)
          ? await ToCurrency.GetRewardForRedeemAsync()
          : 0;

      this.CallUIRefresh();
    }

    public async Task<string> Send()
    {
      try
      {
        var error = await ConvertAsync();

        if (error != null)
        {
          Console.WriteLine(error.Description); // todo: go back to confirmation;
          return error?.Description ?? "An error has occurred while sending swap.";
        }

        Console.WriteLine("Swap successfully created");
        return null;
      }
      catch (Exception e)
      {
        return "An error has occurred while sending swap.";
      }
    }

    private async Task<Error> ConvertAsync()
    {
      try
      {
        var account = App.Account;

        var fromWallets = (await account
            .GetUnspentAddressesAsync(
                toAddress: null,
                currency: FromCurrency.Name,
                amount: Amount,
                fee: 0,
                feePrice: await FromCurrency.GetDefaultFeePriceAsync(),
                feeUsagePolicy: FeeUsagePolicy.EstimatedFee,
                addressUsagePolicy: AddressUsagePolicy.UseMinimalBalanceFirst,
                transactionType: BlockchainTransactionType.SwapPayment))
            .ToList();

        if (Amount == 0)
          return new Error(Errors.SwapError, "Amount to convert must be greater than zero.");

        if (Amount > 0 && !fromWallets.Any())
          return new Error(Errors.SwapError, "Insufficient funds");

        var symbol = App.Account.Symbols.SymbolByCurrencies(FromCurrency, ToCurrency);
        var baseCurrency = App.Account.Currencies.GetByName(symbol.Base);
        var side = symbol.OrderSideForBuyCurrency(ToCurrency);
        var terminal = App.Terminal;
        var price = EstimatedPrice;
        var orderPrice = EstimatedOrderPrice;

        if (price == 0)
          return new Error(Errors.NoLiquidity, "Not enough liquidity to convert a specified amount.");

        var qty = AmountHelper.AmountToQty(side, Amount, price, baseCurrency.DigitsMultiplier);

        if (qty < symbol.MinimumQty)
        {
          var minimumAmount = AmountHelper.QtyToAmount(side, symbol.MinimumQty, price, FromCurrency.DigitsMultiplier);
          var message = string.Format(CultureInfo.InvariantCulture, "The amount must be greater than or equal to the minimum allowed amount {0} {1}.", minimumAmount, FromCurrency.Name);

          return new Error(Errors.SwapError, message);
        }

        var order = new Order
        {
          Symbol = symbol.Name,
          TimeStamp = DateTime.UtcNow,
          Price = orderPrice,
          Qty = qty,
          Side = side,
          Type = OrderType.FillOrKill,
          FromWallets = fromWallets.ToList()
        };

        await order.CreateProofOfPossessionAsync(account);

        terminal.OrderSendAsync(order);

        // wait for swap confirmation
        var timeStamp = DateTime.UtcNow;

        while (DateTime.UtcNow < timeStamp + SwapTimeout)
        {
          await Task.Delay(SwapCheckInterval);

          var currentOrder = terminal.Account.GetOrderById(order.ClientOrderId);

          if (currentOrder == null)
            continue;

          if (currentOrder.Status == OrderStatus.Pending)
            continue;

          if (currentOrder.Status == OrderStatus.PartiallyFilled || currentOrder.Status == OrderStatus.Filled)
          {
            var swap = (await terminal.Account
                .GetSwapsAsync())
                .FirstOrDefault(s => s.OrderId == currentOrder.Id);

            if (swap == null)
              continue;

            return null;
          }

          if (currentOrder.Status == OrderStatus.Canceled)
            return new Error(Errors.PriceHasChanged, "Oops, the price has changed during the order sending. Please try again.");

          if (currentOrder.Status == OrderStatus.Rejected)
            return new Error(Errors.OrderRejected, "Order rejected.");
        }

        return new Error(Errors.TimeoutReached, "Atomex is not responding for a long time.");
      }
      catch (Exception e)
      {
        Log.Error(e, "Conversion error");

        return new Error(Errors.SwapError, "Conversion error. Please contant technical support.");
      }
    }

    public string NotReadyConvertMessage
    {
      get
      {
        if (Amount == 0)
        {
          var msg = "Amount to convert must be greater than zero.";
          return msg;
        }

        if (EstimatedPrice == 0)
        {
          var msg = "Not enough liquidity to convert a specified amount.";
          return msg;
        }

        if (!App.Terminal.IsServiceConnected(TerminalService.All))
        {
          var msg = "Atomex services unavailable. Please check your network connection or contact technical support.";
          return msg;
        }

        var symbol = Symbols.SymbolByCurrencies(FromCurrency, ToCurrency);
        if (symbol == null)
        {
          var msg = "This symbol does not support direct conversion.";
          return msg;
        }

        var side = symbol.OrderSideForBuyCurrency(ToCurrency);
        var price = EstimatedPrice;
        var baseCurrency = Currencies.GetByName(symbol.Base);
        var qty = AmountHelper.AmountToQty(side, Amount, price, baseCurrency.DigitsMultiplier);

        if (qty < symbol.MinimumQty)
        {
          var minimumAmount = AmountHelper.QtyToAmount(side, symbol.MinimumQty, price, FromCurrency.DigitsMultiplier);

          var msg = "The amount must be greater than or equal to the minimum allowed amount";
          var message = string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}", msg, minimumAmount, FromCurrency.Name);

          return message;
        }

        return null;
      }
    }

  }
}
