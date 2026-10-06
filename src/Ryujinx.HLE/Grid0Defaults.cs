namespace Ryujinx.HLE
{
    /// <summary>
    /// The GRID0+ server the private-server settings point at out of the box.
    /// </summary>
    /// <remarks>
    /// Two addresses because Pia's NAT check needs nncs2 answered from a different address
    /// than nncs1; see <see cref="HleConfiguration.PrivateServerNatCheckSecondaryAddress"/>.
    /// </remarks>
    public static class Grid0Defaults
    {
        public const string Address = "89.168.58.206";
        public const string NatCheckSecondaryAddress = "145.241.167.178";
    }
}
