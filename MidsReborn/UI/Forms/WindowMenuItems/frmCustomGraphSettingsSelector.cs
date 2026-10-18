using System;
using System.Linq;
using System.Windows.Forms;
using Mids_Reborn.Core;

namespace Mids_Reborn.UI.Forms.WindowMenuItems
{
    public partial class frmCustomGraphSettingsSelector : Form
    {
        private enum StatType
        {
            Damage,
            Mez,
            Effect
        }

        private CustomGraphStat.eCustomGraphStat Stat;
        public ConfigData.CustomGraphSettings Settings { get; }

        public frmCustomGraphSettingsSelector(CustomGraphStat.eCustomGraphStat stat, ConfigData.CustomGraphSettings initialSettings)
        {
            InitializeComponent();
            Stat = stat;
            Settings = initialSettings;
        }

        private void frmCustomGraphSettingsSelector_Load(object sender, EventArgs e)
        {
            Text = $"Options for {Stat} graph";
            // Read before the radio buttons below reset the selection.
            var initialVector = CurrentVectorIndex();
            switch (GetStatType())
            {
                case StatType.Damage:
                    switch (Settings.DamageMode)
                    {
                        case CustomGraphStat.eCustomGraphMode.Single:
                            radioButton1.Checked = true;
                            break;

                        case CustomGraphStat.eCustomGraphMode.Min:
                            radioButton2.Checked = true;
                            break;

                        case CustomGraphStat.eCustomGraphMode.Max:
                            radioButton3.Checked = true;
                            break;

                        case CustomGraphStat.eCustomGraphMode.Average:
                            radioButton4.Checked = true;
                            break;
                    }

                    break;

                case StatType.Effect:
                    switch (Settings.EffectMode)
                    {
                        case CustomGraphStat.eCustomGraphMode.Single:
                            radioButton1.Checked = true;
                            break;

                        case CustomGraphStat.eCustomGraphMode.Min:
                            radioButton2.Checked = true;
                            break;

                        case CustomGraphStat.eCustomGraphMode.Max:
                            radioButton3.Checked = true;
                            break;

                        case CustomGraphStat.eCustomGraphMode.Average:
                            radioButton4.Checked = true;
                            break;
                    }

                    break;

                case StatType.Mez:
                    switch (Settings.MezMode)
                    {
                        case CustomGraphStat.eCustomGraphMode.Single:
                            radioButton1.Checked = true;
                            break;

                        case CustomGraphStat.eCustomGraphMode.Min:
                            radioButton2.Checked = true;
                            break;

                        case CustomGraphStat.eCustomGraphMode.Max:
                            radioButton3.Checked = true;
                            break;

                        case CustomGraphStat.eCustomGraphMode.Average:
                            radioButton4.Checked = true;
                            break;
                    }

                    break;
            }

            var vectors = AvailableVectors();
            cbVector.SuspendLayout();
            cbVector.Items.Clear();
            foreach (var v in vectors)
            {
                cbVector.Items.Add(v);
            }

            cbVector.SelectedIndex = Math.Max(0, initialVector);

            cbVector.ResumeLayout(true);
        }

        private StatType GetStatType()
        {
            return Stat switch
            {
                CustomGraphStat.eCustomGraphStat.DebuffResistance => StatType.Effect,
                CustomGraphStat.eCustomGraphStat.Defense or CustomGraphStat.eCustomGraphStat.Resistance or CustomGraphStat.eCustomGraphStat.Elusivity => StatType.Damage,
                _ => StatType.Mez
            };
        }

        // Explicit enum values offered for each graph; the combo box index maps directly into these arrays.
        // They're CustomGraphStat's lists, so the min/max/average modes summarize the same vectors.
        private static Enums.eEffectType[] DebuffEffects => CustomGraphStat.DebuffEffectsList;

        private static Enums.eMez[] MezTypes => CustomGraphStat.MezList;

        private Enums.eDamage[] AvailableDamageVectors()
        {
            return Stat switch
            {
                CustomGraphStat.eCustomGraphStat.Elusivity => CustomGraphStat.ElusivityVectors,
                CustomGraphStat.eCustomGraphStat.Defense => CustomGraphStat.DefenseVectors,
                _ => CustomGraphStat.ResistanceVectors
            };
        }

        private string[] AvailableVectors()
        {
            return GetStatType() switch
            {
                StatType.Damage => AvailableDamageVectors()
                    .Select(e => e == Enums.eDamage.None ? "Untyped" : e.ToString())
                    .ToArray(),
                StatType.Effect => DebuffEffects.Select(e => e.ToString()).ToArray(),
                _ => MezTypes.Select(e => e.ToString()).ToArray()
            };
        }

        private int CurrentVectorIndex()
        {
            return GetStatType() switch
            {
                StatType.Damage => Settings.DamageType is { } d ? Array.IndexOf(AvailableDamageVectors(), d) : -1,
                StatType.Effect => Settings.EffectTypeAux is { } fx ? Array.IndexOf(DebuffEffects, fx) : -1,
                _ => Settings.MezType is { } m ? Array.IndexOf(MezTypes, m) : -1
            };
        }

        private void SetSelectedVector(int index)
        {
            switch (GetStatType())
            {
                case StatType.Damage:
                    var damageVectors = AvailableDamageVectors();
                    Settings.DamageType = index >= 0 && index < damageVectors.Length ? damageVectors[index] : null;

                    break;

                case StatType.Effect:
                    Settings.EffectTypeAux = index >= 0 && index < DebuffEffects.Length ? DebuffEffects[index] : null;

                    break;

                default:
                    Settings.MezType = index >= 0 && index < MezTypes.Length ? MezTypes[index] : null;

                    break;
            }
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        private void btnOk_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.OK;
            Close();
        }

        private void radioButton1_CheckedChanged(object sender, EventArgs e)
        {
            label2.Enabled = radioButton1.Checked;
            cbVector.Enabled = radioButton1.Checked;

            if (!radioButton1.Checked)
            {
                return;
            }

            radioButton2.Checked = false;
            radioButton3.Checked = false;
            radioButton4.Checked = false;

            switch (GetStatType())
            {
                case StatType.Damage:
                    Settings.DamageMode = CustomGraphStat.eCustomGraphMode.Single;
                    break;

                case StatType.Effect:
                    Settings.EffectMode = CustomGraphStat.eCustomGraphMode.Single;
                    break;

                case StatType.Mez:
                    Settings.MezMode = CustomGraphStat.eCustomGraphMode.Single;
                    break;
            }

            SetSelectedVector(cbVector.SelectedIndex);
        }

        private void radioButton2_CheckedChanged(object sender, EventArgs e)
        {
            if (!radioButton2.Checked)
            {
                return;
            }

            radioButton1.Checked = false;
            radioButton3.Checked = false;
            radioButton4.Checked = false;

            label2.Enabled = radioButton1.Checked;
            cbVector.Enabled = radioButton1.Checked;

            switch (GetStatType())
            {
                case StatType.Damage:
                    Settings.DamageMode = CustomGraphStat.eCustomGraphMode.Min;
                    break;

                case StatType.Effect:
                    Settings.EffectMode = CustomGraphStat.eCustomGraphMode.Min;
                    break;

                case StatType.Mez:
                    Settings.MezMode = CustomGraphStat.eCustomGraphMode.Min;
                    break;
            }

            SetSelectedVector(-1);
        }

        private void radioButton3_CheckedChanged(object sender, EventArgs e)
        {
            if (!radioButton3.Checked)
            {
                return;
            }

            radioButton1.Checked = false;
            radioButton2.Checked = false;
            radioButton4.Checked = false;

            label2.Enabled = radioButton1.Checked;
            cbVector.Enabled = radioButton1.Checked;

            switch (GetStatType())
            {
                case StatType.Damage:
                    Settings.DamageMode = CustomGraphStat.eCustomGraphMode.Max;
                    break;

                case StatType.Effect:
                    Settings.EffectMode = CustomGraphStat.eCustomGraphMode.Max;
                    break;

                case StatType.Mez:
                    Settings.MezMode = CustomGraphStat.eCustomGraphMode.Max;
                    break;
            }

            SetSelectedVector(-1);
        }

        private void radioButton4_CheckedChanged(object sender, EventArgs e)
        {
            if (!radioButton4.Checked)
            {
                return;
            }

            radioButton1.Checked = false;
            radioButton2.Checked = false;
            radioButton3.Checked = false;

            label2.Enabled = radioButton1.Checked;
            cbVector.Enabled = radioButton1.Checked;

            switch (GetStatType())
            {
                case StatType.Damage:
                    Settings.DamageMode = CustomGraphStat.eCustomGraphMode.Average;
                    break;

                case StatType.Effect:
                    Settings.EffectMode = CustomGraphStat.eCustomGraphMode.Average;
                    break;

                case StatType.Mez:
                    Settings.MezMode = CustomGraphStat.eCustomGraphMode.Average;
                    break;
            }

            SetSelectedVector(-1);
        }

        private void cbVector_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cbVector.SelectedIndex < 0)
            {
                return;
            }

            SetSelectedVector(cbVector.SelectedIndex);
        }
    }
}
