using ClubShell.Windows.Hardware;
using ClubShell.Windows.Registry;

namespace ClubShell.Windows.Tests;

/// <summary>
/// The disk serial in the hardware id comes from a local disk only (SHELL_CHANGES 30), and registration knows when Windows
/// setup is still running (35).
/// </summary>
public sealed class HwidDiskAndSetupTests
{
    private const int Iscsi = 9;
    private const int Usb = 7;
    private const int Nvme = 17;
    private const int Sata = 11;

    [Fact]
    public void A_diskless_seat_whose_disk_0_is_iscsi_contributes_no_disk_serial()
    {
        var drives = new[] { Drive(0, "ISCSI-SEAT-12") };
        HardwareInventory.HwidDiskSerial(drives, [Physical(0, Iscsi)]).Should().BeNull();
    }

    [Fact]
    public void A_hybrid_takes_its_local_disk_whatever_index_the_iscsi_games_disk_got()
    {
        var drives = new[] { Drive(0, "ISCSI-GAMES"), Drive(1, "NVME-LOCAL-SSD"), Drive(2, "USB-STICK", "Removable Media") };
        var physical = new[] { Physical(0, Iscsi), Physical(1, Nvme), Physical(2, Usb) };
        HardwareInventory.HwidDiskSerial(drives, physical).Should().Be("NVME-LOCAL-SSD");
    }

    [Fact]
    public void An_ordinary_pc_keeps_the_serial_of_disk_0_so_its_id_does_not_change()
    {
        var drives = new[] { Drive(0, "SATA-SYSTEM"), Drive(1, "SATA-DATA") };
        HardwareInventory.HwidDiskSerial(drives, [Physical(0, Sata), Physical(1, Sata)]).Should().Be("SATA-SYSTEM");
    }

    [Fact]
    public void Without_bus_types_disk_0_is_taken_as_before()
    {
        HardwareInventory.HwidDiskSerial([Drive(0, "WHATEVER-0"), Drive(1, "WHATEVER-1")], []).Should().Be("WHATEVER-0");
        HardwareInventory.HwidDiskSerial([], []).Should().BeNull();
    }

    [Fact]
    public void A_local_disk_with_removable_or_external_media_is_skipped()
    {
        var drives = new[] { Drive(0, "SATA-DOCK", "External hard disk media"), Drive(1, "SATA-INSIDE") };
        HardwareInventory.HwidDiskSerial(drives, [Physical(0, Sata), Physical(1, Sata)]).Should().Be("SATA-INSIDE");
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, true)]
    [InlineData(0, 1, true)]
    public void Setup_is_in_progress_while_either_value_is_1(int? systemSetup, int? oobe, bool expected) =>
        WindowsSetup.IsInProgress(name => name switch
        {
            "SystemSetupInProgress" => systemSetup,
            "OOBEInProgress" => oobe,
            _ => throw new ArgumentException(name),
        }).Should().Be(expected);

    private static WmiDiskDrive Drive(int index, string serial, string media = "Fixed hard disk media") =>
        new(index, "Model", serial, 512L << 30, "SCSI", media);

    private static WmiPhysicalDisk Physical(int number, int bus) => new(number, "Disk", "", 512L << 30, 4, bus);
}
