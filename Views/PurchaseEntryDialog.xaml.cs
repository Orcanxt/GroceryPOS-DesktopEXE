using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using GroceryPOS.Models;

namespace GroceryPOS.Views
{
    public partial class PurchaseEntryDialog : Window
    {
        private readonly ObservableCollection<PurchaseLineItem> _rows = new();
        private readonly ObservableCollection<SupplierOption> _supplierOptions = new();
        public PurchaseEntry? Result { get; private set; }

        // Exposed for the in-grid item dropdown to bind to. Mutable behind the scenes so a
        // product created from this dialog is pickable on the very next line.
        private readonly List<Product> _catalog;
        public IReadOnlyList<Product> ProductCatalog => _catalog;

        /// <summary>Host opens the product form, persists whatever it creates, and hands the
        /// product back so this invoice can use it. Null means the host cancelled. The
        /// argument seeds the barcode field — set when an unknown code was just scanned.</summary>
        public Func<string?, Product?>? NewProductRequested { get; set; }

        // The picker offers the whole catalogue, so the cap is only a guard against a very
        // large product list building an unusably long popup in one go.
        private const int PickerMax = 400;

        public PurchaseEntryDialog(IEnumerable<SupplierOption> suppliers, IEnumerable<Product> products, decimal previousBalanceForFirst = 0m, string suggestedInvoice = "", PurchaseEntry? existing = null)
        {
            _catalog = (products ?? Enumerable.Empty<Product>())
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .OrderBy(p => p.Name)
                .ToList();

            InitializeComponent();

            // Fit this dialog's layout to the screen on small/scaled POS displays.
            Helpers.UiScaler.Fit(this);

            foreach (var s in (suppliers ?? Enumerable.Empty<SupplierOption>()).Where(s => !string.IsNullOrWhiteSpace(s.Name)))
                _supplierOptions.Add(s);
            CboSupplier.ItemsSource = _supplierOptions;

            CboPayMode.ItemsSource  = Enum.GetNames(typeof(PayMode));

            ItemsGrid.ItemsSource = _rows;

            if (existing != null)
            {
                // Editing an existing invoice — prefill every field. Line items are cloned
                // so edits stay in the dialog until the caller reverses/re-applies on save.
                Title = "Edit Purchase Invoice";
                if (TxtDialogTitle != null) TxtDialogTitle.Text = "✎  EDIT PURCHASE INVOICE";
                CboSupplier.Text        = existing.SupplierName;
                TxtSupplierTrn.Text     = existing.SupplierTrn ?? "";
                _autoFilledTrn          = TxtSupplierTrn.Text;
                TxtInvoice.Text         = existing.InvoiceNumber;
                DpDate.SelectedDate     = existing.Date;
                CboPayMode.SelectedItem = existing.PayMethod.ToString();
                TxtNotes.Text           = existing.Notes ?? "";
                TxtPaid.Text            = existing.AmountPaid.ToString("0.##");
                foreach (var it in existing.Items)
                    _rows.Add(new PurchaseLineItem { ItemName = it.ItemName, Barcode = it.Barcode, Qty = it.Qty,
                                                     Rate = it.Rate, RetailPrice = it.RetailPrice,
                                                     TaxPercent = it.TaxPercent, DiscountPercent = it.DiscountPercent });
                if (_rows.Count == 0) _rows.Add(NewBlankRow());

                // The discount box shows what this invoice actually gave, as a percentage of
                // its value. An invoice keyed before the discount moved to the foot of the
                // bill can carry a different one on every line, and only one figure can be
                // shown — so it shows the discount overall, and re-saving spreads that evenly
                // across the lines: the invoice total stands, a fil or two moves between them.
                decimal priorGross = existing.Items.Sum(i => i.Gross);
                decimal priorDisc  = existing.Items.Sum(i => i.DiscountAmount);
                TxtDiscInput.Text  = priorGross > 0m && priorDisc > 0m
                                   ? (priorDisc / priorGross * 100m).ToString("0.####")
                                   : "0";
            }
            else
            {
                CboPayMode.SelectedItem = nameof(PayMode.Cash);
                DpDate.SelectedDate = DateTime.Today;
                TxtInvoice.Text     = suggestedInvoice;
                _rows.Add(NewBlankRow());
            }

            ShowDiscountUnit();
            HookRowEvents();
            _rows.CollectionChanged += (_, e) =>
            {
                if (e.NewItems != null)
                    foreach (PurchaseLineItem r in e.NewItems) r.PropertyChanged += OnRowChanged;
                if (e.OldItems != null)
                    foreach (PurchaseLineItem r in e.OldItems) r.PropertyChanged -= OnRowChanged;
                RecalcTotals();
            };

            CboSupplier.SelectionChanged += (_, _) => SupplierChanged?.Invoke(CboSupplier.Text);
            CboSupplier.LostFocus        += (_, _) => SupplierChanged?.Invoke(CboSupplier.Text);

            SetPreviousBalance(previousBalanceForFirst);
            RecalcTotals();
        }

        /// <summary>Caller wires this to look up the prior outstanding balance for the chosen supplier.</summary>
        public event Action<string>? SupplierChanged;

        /// <summary>Raised when the operator asks to see — and settle — what this supplier
        /// is owed. The host holds the invoices, returns and payments, so it opens the
        /// statement; this dialog only knows whose it is.</summary>
        public event Action<string>? SupplierStatementRequested;

        /// <summary>The supplier as the box currently reads, trimmed.</summary>
        public string SupplierNameText => (CboSupplier.Text ?? "").Trim();

        private void BtnSupplierBalances_Click(object sender, RoutedEventArgs e)
        {
            // A statement is one supplier's account, so there is nothing to show until the
            // invoice says whose delivery this is.
            string name = SupplierNameText;
            if (name.Length == 0)
            {
                MessageBox.Show("Choose the supplier first — a balance belongs to one supplier's account.",
                    "Supplier Balance", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SupplierStatementRequested?.Invoke(name);
        }

        // The TRN this dialog filled in on the operator's behalf. Kept so a supplier change
        // can replace it without ever overwriting a number the operator typed themselves.
        private string _autoFilledTrn = "";

        /// <summary>Host supplies the chosen supplier's TRN from the supplier master.</summary>
        public void SetSupplierTrn(string? trn)
        {
            trn = (trn ?? "").Trim();
            string current = (TxtSupplierTrn.Text ?? "").Trim();

            // Only a blank box, or one still holding the last auto-filled number, is
            // replaced. A TRN typed by hand survives a supplier re-selection — the event
            // fires again on every lost focus, and re-filling would quietly undo the typing.
            if (current.Length == 0 || current == _autoFilledTrn)
            {
                TxtSupplierTrn.Text = trn;
                _autoFilledTrn      = trn;
            }
            UpdateTrnHint();
        }

        /// <summary>The TRN as keyed, spaces and dashes removed.</summary>
        public string SupplierTrn =>
            new string((TxtSupplierTrn?.Text ?? "").Where(char.IsLetterOrDigit).ToArray());

        private void TxtSupplierTrn_Changed(object sender, TextChangedEventArgs e) => UpdateTrnHint();

        // Says what the number looks like rather than refusing it: plenty of small suppliers
        // are not VAT-registered and have no TRN at all, and a shop should still be able to
        // record buying from them.
        private void UpdateTrnHint()
        {
            if (TxtTrnHint == null) return;
            string trn = SupplierTrn;
            if (trn.Length == 0)
            {
                TxtTrnHint.Text = "A UAE TRN is 15 digits. Leave blank if the supplier is not VAT-registered.";
                TxtTrnHint.Foreground = (System.Windows.Media.Brush)FindResource("Br_TextMuted");
            }
            else if (trn.Length == 15 && trn.All(char.IsDigit))
            {
                TxtTrnHint.Text = "✓ 15 digits — this invoice can be claimed as a tax invoice.";
                TxtTrnHint.Foreground = (System.Windows.Media.Brush)FindResource("Br_Green");
            }
            else
            {
                TxtTrnHint.Text = $"This is {trn.Length} digit(s); a UAE TRN is 15. It will be saved as typed — check it against the invoice.";
                TxtTrnHint.Foreground = (System.Windows.Media.Brush)FindResource("Br_Amber");
            }
        }

        public void SetPreviousBalance(decimal value)
        {
            _previousBalance = value;
        }
        private decimal _previousBalance;

        private PurchaseLineItem NewBlankRow() => new PurchaseLineItem { Qty = 1m, Rate = 0m, TaxPercent = 5m };

        private void HookRowEvents()
        {
            foreach (var r in _rows) r.PropertyChanged += OnRowChanged;
        }

        private void OnRowChanged(object? s, PropertyChangedEventArgs e)
        {
            // Half-way through sharing the invoice discount out the lines disagree with each
            // other, and a recalc then would only be thrown away — the spread ends in one.
            if (_spreadingDiscount) return;
            RecalcTotals();
        }

        // ─────────── The invoice discount ───────────
        // ONE discount for the whole bill rather than one per line: a supplier states a trade
        // discount at the foot of the invoice, so that is where it is keyed, and there is one
        // figure to check against the supplier's own total instead of twenty.
        //
        // It is still STORED on the lines, the same percentage on each. That IS what a
        // discount on the whole amount means — pro rata to what each line is worth — and
        // keeping it there is what lets everything downstream stand: VAT is still worked out
        // line by line at each line's own rate, PurchaseLineItem.NetRate is still the money
        // actually paid per unit (so a product's cost price, and every margin measured from
        // it, is real money), the purchase reports and the invoice read-back need no discount
        // rule of their own, and an invoice saved before this change still opens.
        private bool _discIsPercent = true;

        // Writing DiscountPercent on a line raises PropertyChanged, which would recalc, which
        // would spread again. This is what stops that going round.
        private bool _spreadingDiscount;

        // The unit the discount is keyed in — a percentage of the items total, or a flat
        // amount off it. Suppliers state both, so the operator says which this invoice is.
        private void BtnDiscUnit_Click(object sender, RoutedEventArgs e)
        {
            // The figure keyed stays exactly as typed and is simply read in the other unit.
            // This button is how a discount keyed in the wrong unit gets corrected, so
            // turning "50" into "10%" to hold the money steady would defeat the point of it.
            _discIsPercent = !_discIsPercent;
            ShowDiscountUnit();
            RecalcTotals();
        }

        private void ShowDiscountUnit()
        {
            if (BtnDiscUnit != null) BtnDiscUnit.Content = _discIsPercent ? "%" : AppSettings.Cur;
        }

        private void TxtDisc_Changed(object sender, TextChangedEventArgs e) => RecalcTotals();

        // Typing over a discount is the normal edit, so the old one is selected on arrival
        // rather than left for the operator to clear digit by digit.
        private void TxtDisc_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => TxtDiscInput?.SelectAll();

        /// <summary>The whole-invoice discount as a percentage of the items total — the form
        /// each line carries its share in.</summary>
        private decimal InvoiceDiscountPercent()
        {
            decimal entered = ParseDecimal(TxtDiscInput?.Text);
            if (entered == 0m) return 0m;
            if (_discIsPercent) return entered;

            // An amount off an invoice worth nothing is a typo, not a discount: resolving it
            // against a zero total would read as everything off, or divide by zero.
            decimal gross = _rows.Sum(r => r.Gross);
            return gross > 0m ? entered / gross * 100m : 0m;
        }

        /// <summary>Share the invoice discount over the lines, in proportion to what each
        /// line is worth — which a single percentage on every line already is.</summary>
        private void SpreadDiscountOverLines()
        {
            if (_spreadingDiscount) return;
            _spreadingDiscount = true;
            try
            {
                decimal pct = InvoiceDiscountPercent();
                foreach (var r in _rows)
                    if (r.DiscountPercent != pct) r.DiscountPercent = pct;
            }
            finally { _spreadingDiscount = false; }
        }

        private void RecalcTotals()
        {
            if (TxtBal == null) return; // fires during XAML init before all totals controls exist
            SpreadDiscountOverLines();  // every line carries its share before anything is summed
            decimal itemsTotal = _rows.Sum(r => r.Gross);
            decimal discount   = _rows.Sum(r => r.DiscountAmount);
            decimal subtotal = _rows.Sum(r => r.Net);
            decimal vat      = _rows.Sum(r => r.Tax);
            decimal grand    = subtotal + vat;
            decimal paid     = ParseDecimal(TxtPaid.Text);
            decimal balance  = grand - paid; // this invoice only; the supplier's running balance is behind SUPPLIER BALANCE

            TxtItemsTotal.Text = $"{AppSettings.Cur} {itemsTotal:N2}";
            // Signed, so a discount reads as money coming off rather than money added.
            TxtDiscount.Text = discount > 0 ? $"- {AppSettings.Cur} {discount:N2}" : $"{AppSettings.Cur} 0.00";
            TxtSubtotal.Text = $"{AppSettings.Cur} {subtotal:N2}";
            TxtVat.Text      = $"{AppSettings.Cur} {vat:N2}";
            TxtGrand.Text    = $"{AppSettings.Cur} {grand:N2}";
            TxtBal.Text      = $"{AppSettings.Cur} {balance:N2}";
        }

        private void TxtPaid_Changed(object sender, TextChangedEventArgs e) => RecalcTotals();

        // ─────────── Typing in a figure cell ───────────
        // A DataGrid cell, left to itself, is selected by a click and edited only by a
        // double-click or by typing a character — and BACKSPACE is not a character, so a
        // cell holding 12.50 could be retyped from scratch but never corrected: the key did
        // nothing at all. On a touchscreen till it is worse, since there is no double-click
        // and no keyboard to type the opening digit with.
        //
        // These three handlers make a figure cell behave like the plain text boxes around
        // it: one tap opens it with the figure selected, and Backspace or Delete clears it
        // and leaves the cursor there, the way a spreadsheet does.

        // One click (or tap) edits. Only cells the operator may type in: the name and
        // barcode columns carry their own text boxes and are read-only at column level, as
        // are Net, Tax and Total, so all of those fall straight through to their own
        // handling.
        private void ItemsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject) is not DataGridCell cell) return;
            if (cell.IsReadOnly || cell.IsEditing) return;

            // Focus the cell and select its row first: BeginEdit acts on the CURRENT cell,
            // and without this the click would open an editor on whichever cell was current
            // before it.
            if (!cell.IsFocused) cell.Focus();
            if (FindAncestor<DataGridRow>(cell) is DataGridRow row && !row.IsSelected) row.IsSelected = true;

            // Not marked handled: the click goes on into the editor that just opened, so the
            // caret lands where the finger did.
            ItemsGrid.BeginEdit(e);
        }

        // Backspace and Delete on a cell that is merely selected. The grid ignores both, so
        // they are turned into what they plainly mean — clear this figure and let me type —
        // rather than being left as the two keys that do nothing.
        private void ItemsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Back && e.Key != Key.Delete) return;

            // Inside an editor already: this is ordinary text editing, hands off.
            if (Keyboard.FocusedElement is TextBox) return;

            var cell = FindAncestor<DataGridCell>(Keyboard.FocusedElement as DependencyObject)
                    ?? FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
            if (cell == null || cell.IsReadOnly || cell.IsEditing) return;

            if (!ItemsGrid.BeginEdit(e)) return;
            if (CurrentCellEditor() is TextBox box)
            {
                box.Clear();
                box.Focus();
            }
            e.Handled = true;
        }

        // Whatever opened the editor — a tap, a key, a double-click — the old figure arrives
        // selected, so the first digit typed replaces it instead of being appended to it.
        // Appending is how 12.50 becomes 12.505 when somebody meant to type 5.
        private void ItemsGrid_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
        {
            if (e.EditingElement is TextBox box)
            {
                box.Focus();
                box.SelectAll();
            }
        }

        // The editor the grid has just opened on the current cell, if it is a text box.
        private TextBox? CurrentCellEditor()
        {
            if (ItemsGrid.CurrentItem != null &&
                ItemsGrid.CurrentCell.Column?.GetCellContent(ItemsGrid.CurrentItem) is TextBox box)
                return box;
            return Keyboard.FocusedElement as TextBox;
        }

        private void BtnAddItem_Click(object sender, RoutedEventArgs e)
        {
            _rows.Add(NewBlankRow());
            ItemsGrid.SelectedItem = _rows[^1];
            ItemsGrid.ScrollIntoView(_rows[^1]);
        }

        // Create a product the shop has never stocked, from inside the invoice that is
        // buying it. The host owns the product list, so it opens the product form and
        // persists the result; this dialog only has to put it on a line and into the picker.
        //
        // Without this the only way to invoice an unknown item was to type its name and let
        // ApplyPurchaseToInventory invent the product on save — which guesses: category
        // PURCHASED, unit pcs, and a selling price equal to what was paid for it, so the
        // product lands on the shelf at 0% margin. Created here it gets a real category,
        // unit, cost and selling price.
        private void BtnNewProduct_Click(object sender, RoutedEventArgs e)
        {
            if (NewProductRequested == null) return;

            var created = NewProductRequested.Invoke(null);
            if (created == null || string.IsNullOrWhiteSpace(created.Name)) return;

            // Keep the picker in the name order it was built in.
            _catalog.Add(created);
            _catalog.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            // Adding a product from inside an invoice means it is being bought, so put it
            // straight on a line. The selected row is reused while it is still blank, so the
            // ordinary "add a row, then name it" flow doesn't strand an empty line.
            var row = ItemsGrid.SelectedItem as PurchaseLineItem;
            if (row == null || !string.IsNullOrWhiteSpace(row.ItemName))
            {
                row = NewBlankRow();
                _rows.Add(row);
            }
            row.ItemName = created.Name;
            row.Barcode  = created.Barcode ?? "";
            if (created.CostPrice > 0)   row.Rate        = created.CostPrice;
            if (created.RetailPrice > 0) row.RetailPrice = created.RetailPrice;

            ItemsGrid.SelectedItem = row;
            ItemsGrid.ScrollIntoView(row);
            RecalcTotals();
        }

        private void BtnRemoveItem_Click(object sender, RoutedEventArgs e)
        {
            if (ItemsGrid.SelectedItem is PurchaseLineItem row) _rows.Remove(row);
            else if (_rows.Count > 0) _rows.RemoveAt(_rows.Count - 1);
        }

        // ── Barcode cell: scan a code, get the product ──────────────────────────────
        // A scanner is a keyboard that types fast and presses Enter, so Enter is the trigger
        // rather than any per-keystroke guessing: a partial code would otherwise resolve to
        // whatever product happened to share its opening digits.
        private void BarcodeBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox box) box.SelectAll();   // a scan replaces, it does not append
        }

        private void BarcodeBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBox box && !box.IsKeyboardFocusWithin)
            {
                box.Focus();
                e.Handled = true;
            }
        }

        private void BarcodeBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Tab) return;
            if (sender is not TextBox box) return;
            if (ResolveBarcode(box, offerToCreate: e.Key == Key.Enter) && e.Key == Key.Enter)
                e.Handled = true;
        }

        // Leaving the cell resolves too, for a scanner set to send Tab, or a code typed and
        // then clicked away from. It never offers to create: a half-typed code the operator
        // is walking away from is not a request to add a product.
        private void BarcodeBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox box) ResolveBarcode(box, offerToCreate: false);
        }

        // Fills the row from the product carrying this barcode. Returns true when the code
        // was dealt with — matched, or answered by creating a product.
        private bool ResolveBarcode(TextBox box, bool offerToCreate)
        {
            if (box.DataContext is not PurchaseLineItem row) return false;

            string code = (row.Barcode ?? "").Trim();
            if (code.Length == 0) return false;

            var product = _catalog.FirstOrDefault(p =>
                string.Equals((p.Barcode ?? "").Trim(), code, StringComparison.OrdinalIgnoreCase));

            if (product != null)
            {
                _pickingItem = true;
                try
                {
                    // A scan FILLS WHAT IS EMPTY AND OVERWRITES NOTHING. It used to assign the
                    // catalogue name over whatever the line already said, which loses work the
                    // operator had already done: a line named by hand, or named from the picker
                    // and then scanned to record the code, came back reworded — and a name half
                    // typed when the scanner fired was simply gone.
                    //
                    // The same rule covers the rate, or "don't replace" would hold for the name
                    // while the cost was still rewritten underneath it. A rate already keyed is
                    // what this delivery charged; the catalogue's cost is only a starting guess.
                    if (string.IsNullOrWhiteSpace(row.ItemName)) row.ItemName = product.Name;
                    if (row.Rate <= 0m && product.CostPrice > 0) row.Rate = product.CostPrice;
                    if (row.RetailPrice <= 0m && product.RetailPrice > 0) row.RetailPrice = product.RetailPrice;

                    // The code itself is not "already on the line" in that sense — it is the
                    // thing just scanned into this cell. Echoing the stored form normalises a
                    // scan that came in without a leading zero onto the code the product is
                    // actually held under, which is what every later lookup joins on.
                    row.Barcode = product.Barcode;
                }
                finally { _pickingItem = false; }
                return true;
            }

            // Unknown code. A product already named on this line is left alone — the operator
            // may be recording the supplier's own code against a product picked by name.
            if (!offerToCreate || !string.IsNullOrWhiteSpace(row.ItemName)) return false;
            if (NewProductRequested == null) return false;

            if (MessageBox.Show($"No product has the barcode {code}.\n\nCreate it now?",
                                "Unknown Barcode", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return false;

            var created = NewProductRequested.Invoke(code);
            if (created == null || string.IsNullOrWhiteSpace(created.Name)) return false;

            _catalog.Add(created);
            _catalog.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            _pickingItem = true;
            try
            {
                row.ItemName = created.Name;
                row.Barcode  = created.Barcode ?? code;   // the form may have generated its own
                if (created.CostPrice > 0)   row.Rate        = created.CostPrice;
                if (created.RetailPrice > 0) row.RetailPrice = created.RetailPrice;
            }
            finally { _pickingItem = false; }
            return true;
        }

        // ── Minting a code for goods that carry none ────────────────────────────────
        // A sack of rice from a local supplier arrives with nothing printed on it, and the
        // shop still has to put it through the till. Generating the code here rather than
        // only in the product form matters because the invoice is where the goods are first
        // met: the code goes on the line, the line creates the product on save, and the
        // shelf label can be printed from the same number.
        //
        // The format matches the product form's 🎲 Gen exactly — EAN-13 with the in-store
        // "20" prefix and a real check digit — so a generated code scans like any other.
        // The alternative was the PUR-timestamp placeholder ApplyPurchaseToInventory falls
        // back to, which no scanner can read.
        private readonly Random _rnd = new();

        private void BtnGenLineBarcode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not PurchaseLineItem row) return;

            // A product already in the catalogue keeps the code it already has. Minting a
            // second one would not change the product — the line would simply carry a code
            // that matches nothing, and FindPurchasedProduct would fall back to the name.
            var known = _catalog.FirstOrDefault(p =>
                string.Equals((p.Name ?? "").Trim(), (row.ItemName ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (known != null && !string.IsNullOrWhiteSpace(known.Barcode))
            {
                row.Barcode = known.Barcode.Trim();
                MessageBox.Show($"\"{known.Name}\" is already in the product list with barcode {known.Barcode.Trim()}.\n\n" +
                                "That code has been put on the line. Change a product's barcode from the Products tab.",
                                "Product Already Exists", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!string.IsNullOrWhiteSpace(row.Barcode) &&
                MessageBox.Show($"This line already has the barcode {row.Barcode.Trim()}.\n\nReplace it with a newly generated one?",
                                "Generate Barcode", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            row.Barcode = NewUniqueBarcode();
        }

        // Print shelf stickers for the line being keyed.
        //
        // This is where the stickers are actually wanted: the goods are on the counter,
        // the invoice is open, and for anything that arrived with no code printed on it
        // the 🎲 beside this button has just minted one. Walking to the Products tab
        // afterwards means finding the item again and remembering which of the delivery
        // needed labelling.
        private void BtnPrintLineLabel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not PurchaseLineItem row) return;

            string barcode = (row.Barcode ?? "").Trim();
            if (barcode.Length == 0)
            {
                MessageBox.Show(
                    "This line has no barcode yet.\n\nScan the code printed on the goods, or press 🎲 " +
                    "to mint an in-store one for goods that carry none.",
                    "Print Barcode", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // The catalogue product where there is one: it carries the shop's own code, the
            // Arabic name and any offer price, none of which the invoice line knows about.
            var known = _catalog.FirstOrDefault(x =>
                           string.Equals((x.Barcode ?? "").Trim(), barcode, StringComparison.OrdinalIgnoreCase))
                     ?? _catalog.FirstOrDefault(x =>
                           string.Equals((x.Name ?? "").Trim(), (row.ItemName ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

            // An item being created by this invoice is not in the catalogue until SAVE, and
            // it is exactly the one that needs labelling, so the line itself is enough to
            // print from. The barcode is the line's either way — a product matched by name
            // may be carrying a different code, and the sticker has to scan as this line.
            var target = new Product
            {
                Barcode     = barcode,
                Code        = known?.Code   ?? "",
                Name        = !string.IsNullOrWhiteSpace(row.ItemName) ? row.ItemName.Trim() : (known?.Name ?? ""),
                NameAr      = known?.NameAr ?? "",
                // What it will SELL for: the price typed on the line where there is one,
                // since that is the decision just made about this delivery, and the
                // product's standing price otherwise. Never the cost — that figure is the
                // shop's own business and must not reach a customer-facing shelf label.
                RetailPrice = row.RetailPrice > 0 ? row.RetailPrice : (known?.RetailPrice ?? 0m),
                HasOffer    = false
            };

            if (target.RetailPrice <= 0m &&
                MessageBox.Show(
                    $"\"{target.Name}\" has no selling price yet, so the sticker would print " +
                    AppSettings.Money(0m) + ".\n\nType a Retail Price on the line first, or untick " +
                    "Price in the preview to leave it off.\n\nOpen the preview anyway?",
                    "No Selling Price", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            var main = (Owner as MainWindow) ?? (Application.Current.MainWindow as MainWindow);
            if (main == null)
            {
                MessageBox.Show("Cannot open the label preview from here.", "Print Barcode",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // One sticker per unit received, which is what the line already says. A weighed
            // line can be fractional, and half a sticker is not a thing.
            int qty = (int)Math.Max(1m, Math.Ceiling(row.Qty));
            main.ShowBarcodeLabelPreview(target, this, qty);
        }
        /// <summary>A 13-digit in-store EAN-13 that collides with neither the catalogue nor
        /// another line on this invoice — two new items keyed one after the other must not
        /// end up sharing a code.</summary>
        private string NewUniqueBarcode()
        {
            bool Taken(string code) =>
                _catalog.Any(p => string.Equals((p.Barcode ?? "").Trim(), code, StringComparison.OrdinalIgnoreCase))
                || _rows.Any(r => string.Equals((r.Barcode ?? "").Trim(), code, StringComparison.OrdinalIgnoreCase));

            for (int attempt = 0; attempt < 100; attempt++)
            {
                var body = new char[12];
                body[0] = '2'; body[1] = '0';
                for (int i = 2; i < 12; i++) body[i] = (char)('0' + _rnd.Next(10));
                string twelve = new string(body);
                string code = twelve + Ean13CheckDigit(twelve);
                if (!Taken(code)) return code;
            }

            // Extremely unlikely fallback: timestamp-based, still 13 digits.
            string ts = "2" + DateTime.Now.ToString("yyMMddHHmmss");
            return ts.Length >= 13 ? ts.Substring(0, 13) : ts.PadRight(13, '0');
        }

        private static char Ean13CheckDigit(string twelve)
        {
            int sum = 0;
            for (int i = 0; i < 12; i++)
            {
                int d = twelve[i] - '0';
                sum += (i % 2 == 0) ? d : d * 3;   // weights 1,3,1,3… from the left
            }
            return (char)('0' + (10 - sum % 10) % 10);
        }

        // ── Dedicated item picker: a plain TextBox + a filtered popup ListBox per cell ──
        // Deterministic live search: every keystroke rebuilds the popup list from the catalog.
        private bool _pickingItem;   // guards the programmatic text update while applying a pick

        // Names of products previously bought from the currently selected supplier.
        // When non-empty, the item picker is restricted to these; empty = whole catalog.
        private readonly HashSet<string> _supplierItems = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Host calls this when the supplier changes, passing the product names
        /// bought from that supplier, so the item dropdown filters to them.</summary>
        public void SetSupplierItems(IEnumerable<string>? names)
        {
            _supplierItems.Clear();
            foreach (var n in names ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(n)) _supplierItems.Add(n.Trim());
        }

        // Every product in the catalogue is offered. It used to be restricted to what this
        // supplier had been bought from before, which hid the rest of the shop's own
        // products behind a rule nobody could see — a first delivery from a new supplier
        // offered nothing at all. The supplier's previous items are still listed FIRST,
        // since those are what an invoice from them most likely holds; the rest of the
        // catalogue follows in name order.
        private List<Product> PickerSource()
            => _supplierItems.Count == 0
                ? _catalog.ToList()
                : _catalog.Where(p => _supplierItems.Contains(p.Name))
                    .Concat(_catalog.Where(p => !_supplierItems.Contains(p.Name)))
                    .ToList();

        // Build and (optionally) open the popup for a picker box. openIfEmpty shows the
        // whole supplier list before the user types (used on focus).
        private void PopulatePicker(TextBox box, bool openIfEmpty)
        {
            var (popup, list) = FindPicker(box);
            if (popup == null || list == null) return;

            string typed = (box.Text ?? "").Trim();
            var src = PickerSource();
            var matches = (typed.Length == 0
                    ? src
                    : src.Where(p => p.Name.IndexOf(typed, StringComparison.OrdinalIgnoreCase) >= 0))
                .Take(PickerMax).ToList();

            list.ItemsSource = matches;
            popup.IsOpen = matches.Count > 0 && box.IsKeyboardFocusWithin
                           && (openIfEmpty || typed.Length > 0);
        }

        private void ItemBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_pickingItem) return;
            if (sender is not TextBox box) return;
            PopulatePicker(box, openIfEmpty: false);
        }

        private void ItemBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is not TextBox box) return;
            box.SelectAll();
            // Open on focus, always: tapping the cell is how the list is reached on a
            // touchscreen till, and it now holds every product rather than a supplier's few.
            PopulatePicker(box, openIfEmpty: true);
        }

        // First click lands the caret in the box rather than only selecting the grid cell.
        private void ItemBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBox box && !box.IsKeyboardFocusWithin)
            {
                box.Focus();
                e.Handled = true;
            }
        }

        private void ItemBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is not TextBox box) return;
            var (popup, _) = FindPicker(box);
            if (popup == null) return;
            // Keep the list open if focus moved into it (the user is clicking an item).
            if (e.NewFocus is DependencyObject nf && IsInside(nf, popup)) return;
            popup.IsOpen = false;
        }

        private void ItemBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box) return;
            var (popup, list) = FindPicker(box);
            if (popup == null || list == null) return;

            if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; }
            else if (e.Key == Key.Enter && popup.IsOpen && list.Items.Count > 0)
            {
                ApplyProduct(box, list.Items[0] as Product);
                popup.IsOpen = false;
                e.Handled = true;
            }
            else if (e.Key == Key.Down && popup.IsOpen && list.Items.Count > 0)
            {
                list.SelectedIndex = 0;
                (list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                e.Handled = true;
            }
        }

        private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListBox list || list.SelectedItem is not Product p) return;
            ApplyProduct(list, p);
            if (FindAncestorLogical<Popup>(list) is Popup popup) popup.IsOpen = false;
        }

        // Push the chosen product into the row this picker element belongs to.
        private void ApplyProduct(FrameworkElement source, Product? p)
        {
            if (p == null || source.DataContext is not PurchaseLineItem row) return;
            _pickingItem = true;
            try
            {
                row.ItemName = p.Name;
                row.Barcode  = p.Barcode ?? "";
                if (p.CostPrice > 0)   row.Rate        = p.CostPrice;
                if (p.RetailPrice > 0) row.RetailPrice = p.RetailPrice;
            }
            finally { _pickingItem = false; }
        }

        // The TextBox and its Popup/ListBox live together in the cell's DataTemplate Grid.
        private static (Popup? popup, ListBox? list) FindPicker(TextBox box)
        {
            if (box.Parent is Grid g)
            {
                var popup = g.Children.OfType<Popup>().FirstOrDefault();
                var list  = (popup?.Child as Border)?.Child as ListBox;
                return (popup, list);
            }
            return (null, null);
        }

        private static bool IsInside(DependencyObject node, Popup popup)
        {
            for (DependencyObject? d = node; d != null; d = LogicalTreeHelper.GetParent(d) ?? VisualTreeHelper.GetParent(d))
                if (ReferenceEquals(d, popup) || ReferenceEquals(d, popup.Child)) return true;
            return false;
        }

        private static T? FindAncestorLogical<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d != null && d is not T) d = LogicalTreeHelper.GetParent(d);
            return d as T;
        }

        private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
            return d as T;
        }

        /// <summary>Raised when a new supplier is quick-added (name, phone) so the
        /// host can persist it to the master supplier list.</summary>
        public event Action<string, string>? SupplierAdded;

        // "+" beside the supplier box — quick-add a supplier (name + phone), then select it.
        private void BtnAddSupplier_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SupplierQuickDialog { Owner = this };
            if (dlg.ShowDialog() != true) return;
            string name = dlg.SupplierName;
            if (string.IsNullOrWhiteSpace(name)) return;

            if (!_supplierOptions.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                _supplierOptions.Add(new SupplierOption { Name = name, Phone = dlg.SupplierPhone });
            CboSupplier.Text = name;

            SupplierAdded?.Invoke(name, dlg.SupplierPhone);
            SupplierChanged?.Invoke(name);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var supplier = (CboSupplier.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(supplier)) { MessageBox.Show("Supplier name required."); return; }

            var validRows = _rows.Where(r => !string.IsNullOrWhiteSpace(r.ItemName) && r.Qty > 0).ToList();
            if (validRows.Count == 0) { MessageBox.Show("Add at least one line item with a name and qty > 0."); return; }

            // A discount that swallows the whole invoice, or a negative one, is refused
            // rather than clamped: at 100% the supplier would be paying us to deliver, and a
            // negative discount is a surcharge that belongs in the cost price. Either is a
            // typo, and silently "fixing" it would hide a wrong figure on a saved invoice.
            decimal discEntered = ParseDecimal(TxtDiscInput.Text);
            decimal discPercent = InvoiceDiscountPercent();
            if (discEntered < 0m || discPercent >= 100m)
            {
                string keyed = _discIsPercent ? $"{discEntered:N2}%" : $"{AppSettings.Cur} {discEntered:N2}";
                MessageBox.Show($"The invoice discount is {keyed} — {discPercent:N2}% of the items total.\n\n" +
                                "A discount must be at least 0 and less than the whole invoice.",
                                "Check Discount", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Keep the time-of-day for today's entries; midnight for back-dated ones — the
            // same rule expenses follow. The X / Z reading covers a session, not a calendar
            // date, so an invoice stamped midnight would fall outside a session that opened
            // at nine and never reach the reading it was actually paid from.
            DateTime date = (DpDate.SelectedDate ?? DateTime.Today).Date;
            if (date == DateTime.Today) date = DateTime.Now;

            // A malformed TRN is questioned, never blocked: it may be a typo, or the supplier
            // may genuinely have no TRN, and only the person holding the invoice can say.
            string trn = SupplierTrn;
            if (trn.Length > 0 && (trn.Length != 15 || !trn.All(char.IsDigit)) &&
                MessageBox.Show($"\"{trn}\" is not a 15-digit UAE TRN.\n\n" +
                                "Input VAT can only be reclaimed on an invoice carrying the supplier's " +
                                "valid TRN. Save it as typed anyway?",
                                "Check TRN", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            Result = new PurchaseEntry
            {
                InvoiceNumber   = TxtInvoice.Text.Trim(),
                SupplierName    = supplier,
                SupplierTrn     = trn,
                Date            = date,
                PayMethod       = Enum.TryParse<PayMode>(CboPayMode.SelectedItem?.ToString(), out var pm) ? pm : PayMode.Cash,
                Items           = validRows,
                Notes           = TxtNotes.Text ?? "",
                AmountPaid      = ParseDecimal(TxtPaid.Text),
                PreviousBalance = _previousBalance
            };
            DialogResult = true;
        }

        private static decimal ParseDecimal(string? s) => decimal.TryParse(s, out var v) ? v : 0m;
    }

    // A supplier choice shown in the Purchase-Entry supplier dropdown.
    // ToString() returns just the name so the editable box / saved value stays the name,
    // while the dropdown template shows the phone alongside it.
    public class SupplierOption
    {
        public string Name  { get; set; } = "";
        public string Phone { get; set; } = "";
        public string PhoneLabel => string.IsNullOrWhiteSpace(Phone) ? "" : $"📞 {Phone}";
        public override string ToString() => Name;
    }
}
