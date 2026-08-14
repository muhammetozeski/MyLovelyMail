namespace MyLovelyMail.MainProject.DataModels.Events
{
    public readonly record struct BalanceChange(int OldValue, int NewValue)
    {
        public int Delta => NewValue - OldValue;
    }

    public readonly record struct DiamondCredit(string ProductId, int Amount);
}
