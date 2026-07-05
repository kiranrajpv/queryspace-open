-- QuerySpace Template: Post-Load Validation
-- Purpose: Validate that transformed/pre-load material data was loaded correctly to S/4 MDG.
--
-- Tags recognized by Analyze Transform:
-- @template: post-load
-- @phase: post-load-validation
-- @analysis: sap-material-transformation
-- @object: material-master
-- @source: ecc_material_extract
-- @extract: ecc_material_extract
-- @target: mdg_material_preload
-- @preload: mdg_material_preload
-- @postload: s4_mdg_material
-- @xref: material_cross_reference
-- @source-prefix: source_
-- @extract-prefix: source_
-- @target-prefix: transformed_
-- @preload-prefix: transformed_
-- @postload-prefix: mdg_
-- @xref-prefix: xref_
-- @keys: source_matnr,source_werks
-- @compare: source_matnr=transformed_matnr,source_werks=transformed_werks,source_material_type=transformed_material_type,source_base_uom=transformed_base_uom,source_mrp_controller=transformed_mrp_controller,source_procurement_type=transformed_procurement_type
-- @xref-values: transformed_matnr=xref_mdg_matnr,transformed_material_type=xref_mdg_material_type,transformed_base_uom=xref_mdg_uom
-- @xref-valid-from: xref_valid_from
-- @xref-valid-to: xref_valid_to
-- @xref-default-values: UNKNOWN,N/A,UNMAPPED,
-- @numeric-tolerance: 0
-- @date-tolerance-days: 0
-- @business-date: 2026-07-04
-- @rules: null-introduced,value-changed,numeric-drift,date-drift,leading-zero-loss,case-change,trim-change,xref-missing,xref-mismatch,xref-expired,xref-default-used,duplicate-key,postload-mismatch,distribution-shift
--
-- Expected datasets:
-- - ecc_material_extract: source/ECC extract dataset.
-- - mdg_material_preload: transformed pre-load dataset.
-- - s4_mdg_material: post-load extract from MDG/S/4.
-- - material_cross_reference: reference/cross-reference file used by transformation logic.
--
-- How to use:
-- 1. Replace dataset and column names to match your workspace.
-- 2. Run SQL.
-- 3. Click Analyze Transform and review Post-load mismatches.

select
    src.source_matnr,
    src.source_werks,
    src.source_material_type,
    src.source_base_uom,
    src.source_mrp_controller,
    src.source_procurement_type,
    pre.matnr                         as transformed_matnr,
    pre.werks                         as transformed_werks,
    pre.material_type                 as transformed_material_type,
    pre.base_uom                      as transformed_base_uom,
    pre.mrp_controller                as transformed_mrp_controller,
    pre.procurement_type              as transformed_procurement_type,
    mdg.matnr                         as mdg_matnr,
    mdg.werks                         as mdg_werks,
    mdg.material_type                 as mdg_material_type,
    mdg.base_uom                      as mdg_base_uom,
    mdg.mrp_controller                as mdg_mrp_controller,
    mdg.procurement_type              as mdg_procurement_type,
    xref.legacy_matnr                 as xref_legacy_matnr,
    xref.mdg_matnr                    as xref_mdg_matnr,
    xref.mdg_material_type            as xref_mdg_material_type,
    xref.mdg_uom                      as xref_mdg_uom,
    xref.valid_from                   as xref_valid_from,
    xref.valid_to                     as xref_valid_to
from ecc_material_extract src
left join mdg_material_preload pre
    on pre.legacy_matnr = src.source_matnr
   and pre.werks = src.source_werks
left join s4_mdg_material mdg
    on mdg.matnr = pre.matnr
   and mdg.werks = pre.werks
left join material_cross_reference xref
    on xref.legacy_matnr = src.source_matnr
   and xref.legacy_werks = src.source_werks
;
