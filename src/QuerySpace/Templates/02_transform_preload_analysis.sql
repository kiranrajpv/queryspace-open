-- QuerySpace Template: Transform / Pre-Load Analysis
-- Purpose: Compare ECC extract data with transformed MDG pre-load data and confirm cross-reference usage.
--
-- Tags recognized by Analyze Transform:
-- @template: transform
-- @phase: transform-preload
-- @analysis: sap-material-transformation
-- @object: material-master
-- @source: ecc_material_extract
-- @extract: ecc_material_extract
-- @target: mdg_material_preload
-- @preload: mdg_material_preload
-- @xref: material_cross_reference
-- @source-prefix: source_
-- @extract-prefix: source_
-- @target-prefix: transformed_
-- @preload-prefix: transformed_
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
-- @rules: null-introduced,value-changed,numeric-drift,date-drift,leading-zero-loss,case-change,trim-change,xref-missing,xref-mismatch,xref-expired,xref-default-used,duplicate-key,distribution-shift
--
-- Expected datasets:
-- - ecc_material_extract: source/ECC extract dataset.
-- - mdg_material_preload: transformed pre-load file generated before MDG load.
-- - material_cross_reference: reference/cross-reference file used by transformation logic.
--
-- How to use:
-- 1. Replace dataset and column names to match your workspace.
-- 2. Run SQL.
-- 3. Click Analyze Transform.

select
    src.source_matnr,
    src.source_werks,
    src.source_material_type,
    src.source_base_uom,
    src.source_mrp_controller,
    src.source_procurement_type,
    tgt.matnr                          as transformed_matnr,
    tgt.werks                          as transformed_werks,
    tgt.material_type                  as transformed_material_type,
    tgt.base_uom                       as transformed_base_uom,
    tgt.mrp_controller                 as transformed_mrp_controller,
    tgt.procurement_type               as transformed_procurement_type,
    xref.legacy_matnr                  as xref_legacy_matnr,
    xref.mdg_matnr                     as xref_mdg_matnr,
    xref.mdg_material_type             as xref_mdg_material_type,
    xref.mdg_uom                       as xref_mdg_uom,
    xref.valid_from                    as xref_valid_from,
    xref.valid_to                      as xref_valid_to
from ecc_material_extract src
left join mdg_material_preload tgt
    on tgt.legacy_matnr = src.source_matnr
   and tgt.werks = src.source_werks
left join material_cross_reference xref
    on xref.legacy_matnr = src.source_matnr
   and xref.legacy_werks = src.source_werks
;
