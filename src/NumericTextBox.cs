using System;
using System.Globalization;
using System.Windows.Forms;

namespace DellFans
{
    // Plain text editing keeps invalid drafts visible without reserving a native spinner gutter.
    public sealed class NumericTextBox : TextBox
    {
        public decimal Minimum {get;set;}
        public decimal Maximum {get;set;}
        public decimal Increment {get;set;}
        public int DecimalPlaces {get;set;}
        public decimal Value
        {
            get {decimal number;if(!decimal.TryParse(Text,NumberStyles.Number,CultureInfo.CurrentCulture,out number)) throw new FormatException("Invalid numeric draft.");return number;}
            set {if(value<Minimum || value>Maximum) throw new ArgumentOutOfRangeException("value");Text=value.ToString("F"+DecimalPlaces,CultureInfo.CurrentCulture);}
        }
        public NumericTextBox() {BorderStyle=BorderStyle.None;AutoSize=false;TextAlign=HorizontalAlignment.Right;MaxLength=24;Maximum=100;Increment=1;}
        protected override void OnKeyDown(KeyEventArgs e)
        {
            decimal number;
            if((e.KeyCode==Keys.Up || e.KeyCode==Keys.Down) && decimal.TryParse(Text,NumberStyles.Number,CultureInfo.CurrentCulture,out number))
            {Value=Math.Min(Maximum,Math.Max(Minimum,number+(e.KeyCode==Keys.Up ? Increment : -Increment)));e.SuppressKeyPress=true;return;}
            base.OnKeyDown(e);
        }
    }
}
