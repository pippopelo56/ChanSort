﻿using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Schema;
using ChanSort.Api;

namespace ChanSort.Loader.MediaTek;

public class Serializer : SerializerBase
{
  /*
   * Some Android based TVs export an XML file with the format described below.
   * Examples are Philips channel list formats 120 and 125 and Sony BRAVIA 7 (2024).
   * However there are differences between Philips and Sony:
   * - Sony lacks a number of XML elements
   * - Sony seems to manage TV, Radio and Data channels internally in separate lists, all starting at 1, while Philips seems to use one combined list with no duplicate major_channel_numbers
   *
   * <service_list_transfer>
   *   <service_list_infos>
   *     <service_list_info service_list_id="...">
   *       <service_info>
   *         <major_channel_number>
   *         <user_edit_flag>
   *         <service_name>
   *         <sdt_service_type> 1=TV, 2=Radio
   *         <std_stream_content>
   *         <std_stream_content_ext>
   *         <std_stream_component_type>
   *         <record_id>service://SERVICE_LIST_GENERAL_SATELLITE/[service_list_id]/[major_channel_number]
   *         <visible_service>
   *
   *         The following elements exist in the Philips lists but not in the Sony's sdb.xml
   *
   *         <service_id> SID
   *         <transport_stream_id> TSID
   *         <network_id> NID
   *         <frequency> (DVB-S2: MHz)
   *         <original_network_id> ONID
   *         <symbol_rate>
   *         <modulation>
   *         <polarization>
   *         <lock>
   *         <scrambled> 0=false
   *         <satelliteName>
   *   <internal>
   *     <summary> (base64 encoded Java serialized binary)
   *     <scan> (base64 encoded Java serialized binary)
   *     <service_database> (base64 encoded Java serialized binary, which contains proprietary MediaTek compressed/encrypted cl_Zip data)
   */

  private XmlDocument doc;
  private byte[] content;
  private string textContent;
  private readonly StringBuilder fileInfo = new();
  private readonly bool splitTvRadioData;


  #region ctor()
  public Serializer(string inputFile, bool separateTvRadioData = false) : base(inputFile)
  {
    this.splitTvRadioData = separateTvRadioData;
    this.Features.ChannelNameEdit = ChannelNameEditMode.All;
    this.Features.DeleteMode = DeleteMode.NotSupported;
    this.Features.FavoritesMode = FavoritesMode.None;
    this.Features.CanSkipChannels = false;
    this.Features.CanLockChannels = true;
    this.Features.CanHideChannels = false;
    this.Features.CanSaveAs = true;
  }

  #endregion

  #region Load()
  public override void Load()
  {
    bool fail = false;
    try
    {
      this.doc = new XmlDocument();
      this.content = File.ReadAllBytes(this.FileName);
      this.textContent = Encoding.UTF8.GetString(this.content);

      var settings = new XmlReaderSettings
      {
        CheckCharacters = false,
        IgnoreProcessingInstructions = true,
        ValidationFlags = XmlSchemaValidationFlags.None,
        DtdProcessing = DtdProcessing.Ignore
      };
      using var reader = XmlReader.Create(new StringReader(textContent), settings);
      doc.Load(reader);
    }
    catch
    {
      fail = true;
    }

    var root = doc.FirstChild;
    if (root is XmlDeclaration)
      root = root.NextSibling;
    if (fail || root == null || root.LocalName != "service_list_transfer")
      throw LoaderException.TryNext("\"" + this.FileName + "\" is not a supported MediaTek XML file");

    foreach (XmlNode child in root.ChildNodes)
    {
      switch (child.LocalName)
      {
        case "service_list_infos":
          ReadServiceListInfos(child);
          break;
        case "internal":
          // child elements: summary, scan, service_database
          break;
      }
    }
  }
  #endregion

  #region ReadServiceListInfos()
  private void ReadServiceListInfos(XmlNode serviceListInfosNode)
  {
    foreach (var sli in serviceListInfosNode.ChildNodes)
    {
      if (sli is XmlElement serviceListInfo)
        this.ReadServiceList(serviceListInfo);
    }

    foreach (var list in this.DataRoot.ChannelLists)
    {
      list.VisibleColumnFieldNames = ChannelList.DefaultVisibleColumns.ToList();
      list.VisibleColumnFieldNames.Remove("PcrPid");
      list.VisibleColumnFieldNames.Remove("VideoPid");
      list.VisibleColumnFieldNames.Remove("AudioPid");
      list.VisibleColumnFieldNames.Remove("ShortName");
    }
  }
  #endregion

  #region ReadServiceList()
  private void ReadServiceList(XmlElement node)
  {
    var ss = SignalSource.Dvb;
    var slt = node.GetAttribute("service_list_type");
    if (slt.Contains("SATELLITE"))
      ss |= SignalSource.Sat;
    else if (slt.Contains("CABLE"))
      ss |= SignalSource.Cable;
    else if (slt.Contains("TERR"))
      ss |= SignalSource.Antenna;

    // service_list_id example: SERVICE_LIST_GENERAL_SATELLITE/17
    //var serviceListId = node.GetAttribute("service_list_id");

    int idx = 0;
    foreach (var child in node.ChildNodes)
    {
      if (!(child is XmlElement si && si.LocalName == "service_info"))
        continue;

      ReadChannel(si, ss, idx++);
    }
  }
  #endregion

  #region ReadChannel()

  private void ReadChannel(XmlElement si, SignalSource ss, int idx)
  {
    // record_id example: service://SERVICE_LIST_GENERAL_SATELLITE/17/1
    var recIdUri = si.GetElementString("record_id") ?? "";
    var i = recIdUri.LastIndexOf('/');
    var recId = int.Parse("0" + recIdUri.Substring(i + 1));

    var chan = new Channel(ss, recId, -1, "", si);
    chan.RecordOrder = idx;

    chan.OldProgramNr = si.GetElementInt("major_channel_number");
    // user_edit_flag ("none" in all observed records)
    chan.Name = si.GetElementString("service_name");
    chan.ServiceType = si.GetElementInt("sdt_service_type");
    // visible_service ("3" in all observed records)
    chan.ServiceId = si.GetElementInt("service_id");
    chan.TransportStreamId = si.GetElementInt("transport_stream_id");
    chan.FreqInMhz = si.GetElementInt("frequency");
    chan.OriginalNetworkId = si.GetElementInt("original_network_id");
    chan.SymbolRate = si.GetElementInt("symbol_rate");
    // modulation (not used by ChanSort)
    var pol = si.GetElementInt("polarization");
    chan.Polarity = pol == 1 ? 'H' : pol == 2 ? 'V' : '\0';
    chan.Lock = si.GetElementInt("lock") != 0;
    chan.Encrypted = si.GetElementInt("scrambled") != 0;
    chan.Satellite = si.GetElementString("satelliteName");

    if ((ss & SignalSource.Antenna) != 0)
      chan.ChannelOrTransponder = LookupData.Instance.GetDvbtTransponder(chan.FreqInMhz).ToString();
    else if ((ss & SignalSource.Cable) != 0)
      chan.ChannelOrTransponder = LookupData.Instance.GetDvbcTransponder(chan.FreqInMhz).ToString();


    if (splitTvRadioData)
      ss |= LookupData.Instance.IsRadioTvOrData(chan.ServiceType);
    else
      ss |= SignalSource.Tv | SignalSource.Radio | SignalSource.Data;


    var list = DataRoot.GetChannelList(ss);
    if (list == null)
    {
      var name = (ss & SignalSource.Antenna) != 0 ? "Antenna" : (ss & SignalSource.Cable) != 0 ? "Cable" : (ss & SignalSource.Sat) != 0 ? "Sat" : (ss & SignalSource.Ip) != 0 ? "IP" : "Other";
      if (splitTvRadioData)
        name += " " + ((ss & SignalSource.Tv) != 0 ? " TV" : (ss & SignalSource.Radio) != 0 ? " Radio" : " Data");
        
      list = new ChannelList(ss, name);
      this.DataRoot.AddChannelList(list);
    }

    var elements = si.GetElementsByTagName("major_channel_number", si.NamespaceURI);
    list.ReadOnly |= elements.Count == 1 && elements[0].Attributes!["editable", si.NamespaceURI].InnerText == "false";

    list.AddChannel(chan);
  }
  #endregion



  #region GetFileInformation()

  public override string GetFileInformation()
  {
    var txt = base.GetFileInformation();
    return txt + "\n\n" + this.fileInfo;
  }

  #endregion


  #region Save()
  public override void Save()
  {
    foreach (var list in this.DataRoot.ChannelLists)
    {
      foreach (var chan in list.Channels)
      {
        if (chan is not Channel ch || ch.IsProxy)
          continue;

        var si = ch.Xml;
        si["major_channel_number"]!.InnerText = ch.NewProgramNr.ToString();
        si["service_name"]!.InnerText = ch.Name;
        si["visible_service"]!.InnerText = ch.Hidden ? "1" : "3";
        if (si["lock"] != null) // Sony lists don't have this elements
          si["lock"].InnerText = ch.Lock ? "1" : "0";
      }
    }

    var filePath = this.SaveAsFileName ?? this.FileName;
    var settings = new XmlWriterSettings();
    settings.Indent = true;
    settings.Encoding = new UTF8Encoding(false);
    using var w = XmlWriter.Create(filePath, settings);
    this.doc.WriteTo(w);
    this.FileName = filePath;
  }
  #endregion
}
